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
		return await AddAsync(deployment, ct);
	}

	/// <summary>Queues putting <paramref name="target"/>'s build back on the server it ran on.</summary>
	public Task<Deployment> QueueRollbackAsync(Deployment target, CancellationToken ct) => AddAsync(new Deployment
	{
		AppId = target.AppId,
		ServerId = target.ServerId,
		Kind = DeploymentKind.Rollback,
		Revision = target.Revision,
		Commit = target.Commit,
		PlanJson = target.PlanJson,
	}, ct);

	/// <summary>Queues deleting the app from its server.</summary>
	public Task<Deployment> QueueTeardownAsync(App app, CancellationToken ct) => AddAsync(new Deployment
	{
		AppId = app.Id,
		ServerId = app.ServerId,
		Kind = DeploymentKind.Teardown,
	}, ct);

	/// <summary>Whether a run for the app is waiting or in progress.</summary>
	public Task<bool> BusyAsync(int appId, CancellationToken ct) =>
		db.Deployments.AnyAsync(d => d.AppId == appId && (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Running), ct);

	/// <summary>
	/// Whether <paramref name="deployment"/>'s build can be put back: it succeeded on the app's current
	/// server, Jfri saved its plan, and it isn't what's live now.
	/// </summary>
	public async Task<bool> CanRollBackToAsync(App app, Deployment deployment, CancellationToken ct)
	{
		if (deployment.Status != DeploymentStatus.Succeeded || deployment.Kind == DeploymentKind.Teardown
			|| deployment.Revision is null || deployment.PlanJson is null || deployment.ServerId is null || deployment.ServerId != app.ServerId)
		{
			return false;
		}
		// Taking the app down deleted its images.
		var takenDown = await db.Deployments.Where(d => d.AppId == app.Id && d.Kind == DeploymentKind.Teardown && d.Status == DeploymentStatus.Succeeded).MaxAsync(d => (int?)d.Id, ct);
		if (takenDown > deployment.Id)
		{
			return false;
		}
		var live = await db.Deployments.AsNoTracking()
			.Where(d => d.AppId == app.Id && d.Status == DeploymentStatus.Succeeded && d.Kind != DeploymentKind.Teardown)
			.OrderByDescending(d => d.Id).Select(d => d.Revision).FirstOrDefaultAsync(ct);
		return live != deployment.Revision;
	}

	async Task<Deployment> AddAsync(Deployment deployment, CancellationToken ct)
	{
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
			interrupted.Error = "Jfri restarted while this was running. Start it again from the app's page to finish it.";
			interrupted.FinishedAt = DateTime.UtcNow;
			logs.Append(interrupted.Id, interrupted.Error);
		}
		await db.SaveChangesAsync(ct);
		foreach (var id in await db.Deployments.Where(d => d.Status == DeploymentStatus.Queued).OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(ct))
		{
			queue.Enqueue(id);
		}
	}

	/// <summary>Everything one run needs, resolved from its scope.</summary>
	sealed record Run(IServiceProvider Services, JfriDb Db, Vault Vault, Deployment Deployment, App App, Action<string> Log);

	async Task RunAsync(int id, CancellationToken ct)
	{
		using var scope = scopes.CreateScope();
		var services = scope.ServiceProvider;
		var db = services.GetRequiredService<JfriDb>();
		var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.Id == id, ct);
		if (deployment is null || deployment.Status != DeploymentStatus.Queued)
		{
			return;
		}
		var app = await db.Apps.FirstAsync(a => a.Id == deployment.AppId, ct);
		deployment.Status = DeploymentStatus.Running;
		deployment.StartedAt = DateTime.UtcNow;
		await db.SaveChangesAsync(ct);
		var run = new Run(services, db, services.GetRequiredService<Vault>(), deployment, app, line => logs.Append(id, line));

		try
		{
			switch (deployment.Kind)
			{
				case DeploymentKind.Rollback:
					await RollbackAsync(run, ct);
					break;
				case DeploymentKind.Teardown:
					await TeardownAsync(run, ct);
					break;
				default:
					await DeployAsync(run, ct);
					break;
			}
			deployment.Status = DeploymentStatus.Succeeded;
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			deployment.Status = DeploymentStatus.Failed;
			deployment.Error = ex is DeploymentFailedException or ProviderApiException or TimeoutException or InvalidOperationException ? ex.Message : $"Unexpected error: {ex.Message}";
			run.Log($"FAILED: {deployment.Error}");
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

	async Task DeployAsync(Run run, CancellationToken ct)
	{
		var (services, db, vault, deployment, app, log) = run;
		if (deployment.Trigger == "webhook" && app.ServerId is null)
		{
			throw new DeploymentFailedException("Choosing the server", "The app was taken down after this push was queued. Deploy it from Jfri to bring it back.");
		}

		// 1. Source and plan (fresh clone, so the deploy matches the branch head).
		log($"==> Fetching {app.RepoUrl}{(app.Branch is null ? "" : $" ({app.Branch})")}");
		var analyzeError = await services.GetRequiredService<AppService>().AnalyzeAsync(app, ct);
		if (analyzeError is not null)
		{
			throw new DeploymentFailedException("Fetching the source", analyzeError);
		}
		var plan = AppService.Plan(app) ?? throw new DeploymentFailedException("Planning", "Jfri couldn't find a deployable app in the repository.");
		deployment.Commit = app.Commit;
		deployment.Revision = $"{(app.Commit ?? "manual")[..Math.Min(12, (app.Commit ?? "manual").Length)]}-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}".ToLowerInvariant();
		log($"Commit {app.Commit}, plan: {plan.App.Framework} on {plan.Runtime}, needs {plan.Resources}");

		// 2. Secrets: generate missing ones, keep existing ones forever (DB passwords must not rotate under their data).
		var secrets = await SecretsAsync(run, plan, ct);

		// 3. Server.
		var server = await ResolveServerAsync(db, vault, services, deployment, app, plan, log, ct);
		var host = await ConnectAsync(run, plan, server, ct);

		// 4. Deploy.
		var checkout = services.GetRequiredService<JfriPaths>().Workspace(app.Id);
		var url = await new DeploymentEngine().DeployAsync(new DeploymentRequest
		{
			Plan = plan,
			Revision = deployment.Revision,
			Secrets = secrets,
			Host = DeploymentEngine.PublicHost(plan.Domain, server.Host, app.Name),
			Branch = app.Branch ?? "main",
			SshPort = server.Port,
			WriteSource = (stream, token) => GitRepository.ArchiveAsync(checkout, stream, token),
		}, host, log, ct);

		server.Runtime = plan.Runtime.ToString();
		app.ServerId = server.Id;
		app.LiveUrl = url;
		deployment.Url = url;
		deployment.PlanJson = JsonSerializer.Serialize(plan, AppService.Json);
	}

	/// <summary>Puts an earlier build back with the plan it was released with. No fetch, no build.</summary>
	async Task RollbackAsync(Run run, CancellationToken ct)
	{
		var (_, db, _, deployment, app, log) = run;
		if (deployment.Revision is null || deployment.PlanJson is null || deployment.ServerId is null)
		{
			throw new DeploymentFailedException("Rolling back", "This rollback doesn't say which build to put back.");
		}
		var plan = JsonSerializer.Deserialize<DeploymentPlan>(deployment.PlanJson, AppService.Json);
		if (plan is null || plan.AppName != app.Name)
		{
			throw new DeploymentFailedException("Rolling back", "The saved plan for that build doesn't belong to this app.");
		}
		log($"==> Rolling back to {deployment.Revision}{(deployment.Commit is null ? "" : $" (commit {deployment.Commit})")}");
		var secrets = await SecretsAsync(run, plan, ct);
		var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == deployment.ServerId, ct)
			?? throw new DeploymentFailedException("Choosing the server", "The server that build ran on no longer exists.");
		var host = await ConnectAsync(run, plan, server, ct);
		var url = await new DeploymentEngine().RollbackAsync(new DeploymentRequest
		{
			Plan = plan,
			Revision = deployment.Revision,
			Secrets = secrets,
			Host = DeploymentEngine.PublicHost(plan.Domain, server.Host, app.Name),
			Branch = app.Branch ?? "main",
			SshPort = server.Port,
		}, host, log, ct);
		app.LiveUrl = url;
		deployment.Url = url;
	}

	/// <summary>Deletes the app from its server and forgets the server for it. Backups stay on the server.</summary>
	async Task TeardownAsync(Run run, CancellationToken ct)
	{
		var (_, db, vault, deployment, app, log) = run;
		var server = deployment.ServerId is null ? null : await db.Servers.FirstOrDefaultAsync(s => s.Id == deployment.ServerId, ct);
		if (server is null)
		{
			throw new DeploymentFailedException("Choosing the server", "The app's server no longer exists in Jfri, so there's nothing to take down.");
		}
		log($"==> Connecting to {server.Name} ({server.Host})");
		var host = CreateHost(db, vault, server);
		await host.WaitUntilReachableAsync(log, TimeSpan.FromMinutes(2), ct);
		await new DeploymentEngine().TeardownAsync(app.Name, host, log, ct);
		if (app.ServerId == server.Id)
		{
			app.ServerId = null;
			app.LiveUrl = null;
		}
		if (!await db.Apps.AnyAsync(a => a.Id != app.Id && a.ServerId == server.Id, ct))
		{
			log(server.ProviderServerId is null
				? $"Nothing else Jfri deployed runs on {server.Name}."
				: $"Nothing else Jfri deployed runs on {server.Name}. Destroy it on the Connections page to stop paying for it.");
		}
	}

	async Task<IReadOnlyDictionary<string, string>> SecretsAsync(Run run, DeploymentPlan plan, CancellationToken ct)
	{
		var (generated, all) = SecretGenerator.ForDeploy(plan.Secrets, run.Vault.UnprotectMap(run.App.EncryptedSecrets), run.Vault.UnprotectMap(run.App.EncryptedUserSecrets));
		run.App.EncryptedSecrets = run.Vault.ProtectMap(generated);
		await run.Db.SaveChangesAsync(ct);
		return all;
	}

	/// <summary>Checks the app fits on the server, then waits for SSH.</summary>
	static async Task<SshRemoteHost> ConnectAsync(Run run, DeploymentPlan plan, Server server, CancellationToken ct)
	{
		if (await ServerPlacement.ProblemAsync(run.Db, run.App, plan, server, ct) is { } problem)
		{
			throw new DeploymentFailedException("Choosing the server", problem);
		}
		var host = CreateHost(run.Db, run.Vault, server);
		await host.WaitUntilReachableAsync(run.Log, TimeSpan.FromMinutes(6), ct);
		return host;
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
