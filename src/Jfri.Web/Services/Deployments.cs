using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Jfri.Core.Execution;
using Jfri.Core.Generation;
using Jfri.Core.Planning;
using Jfri.Core.Pricing;
using Jfri.Core.Providers;
using Jfri.Core.Sources;
using Jfri.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Services;

/// <summary>Append-only log file per deployment, read back incrementally by the deployment page.</summary>
public sealed class DeploymentLogs(JfriPaths paths)
{
	readonly object _gate = new();

	public void Append(int deploymentId, string line)
	{
		var stamped = $"{DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {line}\n";
		lock (_gate)
		{
			File.AppendAllText(paths.DeploymentLog(deploymentId), stamped);
		}
	}

	/// <summary>Returns log text from byte <paramref name="offset"/> and the new offset.</summary>
	public (string Text, long Offset) Read(int deploymentId, long offset)
	{
		var path = paths.DeploymentLog(deploymentId);
		if (!File.Exists(path))
		{
			return ("", 0);
		}
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		if (offset < 0 || offset > stream.Length)
		{
			offset = 0;
		}
		stream.Seek(offset, SeekOrigin.Begin);
		var buffer = new byte[Math.Min(stream.Length - offset, 512 * 1024)];
		var read = stream.Read(buffer, 0, buffer.Length);
		return (Encoding.UTF8.GetString(buffer, 0, read), offset + read);
	}
}

/// <summary>Deployments wait here until the worker picks them up, one at a time.</summary>
public sealed class DeploymentQueue
{
	readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

	public void Enqueue(int deploymentId) => _channel.Writer.TryWrite(deploymentId);

	public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Creates deployment records and queues them.</summary>
public sealed class DeploymentScheduler(JfriDb db, DeploymentQueue queue)
{
	public async Task<Deployment> QueueAsync(App app, int? serverId, NewServerSpec? newServer, string trigger, CancellationToken ct)
	{
		var deployment = new Deployment
		{
			AppId = app.Id,
			ServerId = serverId,
			NewServerJson = newServer is null ? null : JsonSerializer.Serialize(newServer),
			Trigger = trigger,
		};
		db.Deployments.Add(deployment);
		await db.SaveChangesAsync(ct);
		queue.Enqueue(deployment.Id);
		return deployment;
	}
}

/// <summary>
/// Runs queued deployments: creates the server if needed (recording it before anything else, so a
/// paid server is never lost track of), refreshes the source, plans, keeps generated secrets stable
/// across deploys, and hands off to the <see cref="DeploymentEngine"/>.
/// </summary>
public sealed class DeploymentWorker(IServiceScopeFactory scopes, DeploymentQueue queue, DeploymentLogs logs, ILogger<DeploymentWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await RecoverAsync(stoppingToken);
		await foreach (var id in queue.ReadAllAsync(stoppingToken))
		{
			try
			{
				await RunAsync(id, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Deployment {Id} crashed", id);
			}
		}
	}

	async Task RecoverAsync(CancellationToken ct)
	{
		using var scope = scopes.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
		foreach (var interrupted in await db.Deployments.Where(d => d.Status == DeploymentStatus.Running).ToListAsync(ct))
		{
			interrupted.Status = DeploymentStatus.Failed;
			interrupted.Error = "Jfri restarted while this deployment was running. Deploy again to finish it.";
			interrupted.FinishedAt = DateTime.UtcNow;
			logs.Append(interrupted.Id, interrupted.Error);
		}
		await db.SaveChangesAsync(ct);
		foreach (var id in await db.Deployments.Where(d => d.Status == DeploymentStatus.Queued).OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(ct))
		{
			queue.Enqueue(id);
		}
	}

	async Task RunAsync(int id, CancellationToken ct)
	{
		using var scope = scopes.CreateScope();
		var services = scope.ServiceProvider;
		var db = services.GetRequiredService<JfriDb>();
		var vault = services.GetRequiredService<Vault>();
		var paths = services.GetRequiredService<JfriPaths>();
		var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.Id == id, ct);
		if (deployment is null || deployment.Status != DeploymentStatus.Queued)
		{
			return;
		}
		var app = await db.Apps.FirstAsync(a => a.Id == deployment.AppId, ct);
		deployment.Status = DeploymentStatus.Running;
		deployment.StartedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(ct);
		void Log(string line) => logs.Append(id, line);

		try
		{
			// 1. Source and plan (fresh clone, so the deploy matches the branch head).
			Log($"==> Fetching {app.RepoUrl}{(app.Branch is null ? "" : $" ({app.Branch})")}");
			var appService = services.GetRequiredService<AppService>();
			var analyzeError = await appService.AnalyzeAsync(app, ct);
			if (analyzeError is not null)
			{
				throw new DeploymentFailedException("Fetching the source", analyzeError);
			}
			var plan = AppService.Plan(app) ?? throw new DeploymentFailedException("Planning", "Jfri couldn't find a deployable app in the repository.");
			deployment.Commit = app.Commit;
			deployment.Revision = $"{(app.Commit ?? "manual")[..Math.Min(12, (app.Commit ?? "manual").Length)]}-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}".ToLowerInvariant();
			Log($"Commit {app.Commit}, plan: {plan.App.Framework} on {plan.Runtime}, needs {plan.Resources}");

			// 2. Secrets: generate missing ones, keep existing ones forever (DB passwords must not rotate under their data).
			var secrets = SecretGenerator.Fill(plan.Secrets, vault.UnprotectMap(app.EncryptedSecrets));
			app.EncryptedSecrets = vault.ProtectMap(secrets);
			await db.SaveChangesAsync(ct);

			// 3. Server.
			var server = await ResolveServerAsync(db, vault, services, deployment, app, plan, Log, ct);
			if (server.Runtime is not null && server.Runtime != plan.Runtime.ToString())
			{
				throw new DeploymentFailedException("Choosing the server", $"{server.Name} already runs {server.Runtime}; this app is planned for {plan.Runtime}. Pick another server or change the app's runtime.");
			}
			var host = CreateHost(db, vault, server);
			await host.WaitUntilReachableAsync(Log, TimeSpan.FromMinutes(6), ct);

			// 4. Deploy.
			// sslip.io turns an IP into a hostname Let's Encrypt can issue for; a server added by hostname already has one.
			var publicHost = plan.Domain ?? (System.Net.IPAddress.TryParse(server.Host, out _) ? $"{server.Host.Replace('.', '-').Replace(':', '-')}.sslip.io" : server.Host);
			var checkout = paths.Workspace(app.Id);
			var url = await new DeploymentEngine().DeployAsync(new DeploymentRequest
			{
				Plan = plan,
				Revision = deployment.Revision,
				Secrets = secrets,
				Host = publicHost,
				Branch = app.Branch ?? "main",
				SshPort = server.Port,
				WriteSource = (stream, token) => GitRepository.ArchiveAsync(checkout, stream, token),
			}, host, Log, ct);

			server.Runtime = plan.Runtime.ToString();
			app.ServerId = server.Id;
			app.LiveUrl = url;
			deployment.Url = url;
			deployment.Status = DeploymentStatus.Succeeded;
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			deployment.Status = DeploymentStatus.Failed;
			deployment.Error = ex is DeploymentFailedException or ProviderApiException or TimeoutException or InvalidOperationException ? ex.Message : $"Unexpected error: {ex.Message}";
			Log($"FAILED: {deployment.Error}");
			if (ex is not (DeploymentFailedException or ProviderApiException or TimeoutException))
			{
				logger.LogError(ex, "Deployment {Id} failed", id);
			}
		}
		finally
		{
			deployment.FinishedAt = DateTime.UtcNow;
			await db.SaveChangesAsync(CancellationToken.None);
		}
	}

	async Task<Server> ResolveServerAsync(JfriDb db, Vault vault, IServiceProvider services, Deployment deployment, App app, DeploymentPlan plan, Action<string> log, CancellationToken ct)
	{
		if (deployment.ServerId is { } serverId)
		{
			var existing = await db.Servers.FirstOrDefaultAsync(s => s.Id == serverId, ct)
				?? throw new DeploymentFailedException("Choosing the server", "The selected server no longer exists.");
			log($"==> Using server {existing.Name} ({existing.Host})");
			return existing;
		}
		var spec = JsonSerializer.Deserialize<NewServerSpec>(deployment.NewServerJson ?? "null")
			?? throw new DeploymentFailedException("Choosing the server", "No server was selected.");
		var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == spec.ConnectionId && c.Provider == spec.Provider, ct)
			?? throw new DeploymentFailedException("Creating the server", "The provider connection no longer exists.");
		var provider = services.GetRequiredService<IEnumerable<IServerProvider>>().First(p => p.Info.Id == spec.Provider);
		var token = vault.Unprotect(connection.EncryptedToken);
		var name = ServerProviders.ServerName(app.Name);
		log($"==> Creating server {name} on {provider.Info.Name} ({spec.Size} in {spec.Region})");
		var keys = await SshKeys.GenerateAsync(name, ct);
		var server = new Server
		{
			Name = name,
			Provider = spec.Provider,
			ConnectionId = connection.Id,
			Region = spec.Region,
			Size = spec.Size,
			User = "root",
			EncryptedPrivateKey = vault.Protect(keys.PrivateKey),
			PublicKey = keys.PublicKey,
			Status = ServerStatus.Provisioning,
		};
		db.Servers.Add(server);
		await db.SaveChangesAsync(ct);
		deployment.ServerId = server.Id;

		var created = await provider.CreateAsync(token, new CreateServerRequest(name, spec.Region, spec.Size, keys.PublicKey, spec.Backups), ct);
		server.ProviderServerId = created.Id;
		await db.SaveChangesAsync(ct);
		log($"Server id {created.Id} created; waiting for it to boot");
		var deadline = DateTime.UtcNow.AddMinutes(10);
		var current = created;
		while (current.Ipv4 is null || !current.Running)
		{
			if (DateTime.UtcNow > deadline)
			{
				server.Status = ServerStatus.Failed;
				await db.SaveChangesAsync(ct);
				throw new DeploymentFailedException("Creating the server", "The server didn't start within 10 minutes. Check the provider console; it may still be billing.");
			}
			await Task.Delay(TimeSpan.FromSeconds(5), ct);
			current = await provider.GetAsync(token, created.Id, ct);
		}
		server.Host = current.Ipv4;
		server.Status = ServerStatus.Ready;
		// Remember it right away: if a later step fails, the retry goes to this server instead of buying another.
		app.ServerId = server.Id;
		await db.SaveChangesAsync(ct);
		log($"Server is up at {server.Host}");
		return server;
	}

	static SshRemoteHost CreateHost(JfriDb db, Vault vault, Server server)
	{
		var host = new SshRemoteHost(new SshTarget(server.Host, server.Port, server.User, vault.Unprotect(server.EncryptedPrivateKey), server.KnownHosts));
		host.HostKeyLearned += knownHosts =>
		{
			server.KnownHosts = knownHosts;
			db.SaveChanges();
		};
		return host;
	}
}
