using Jfri.Core.Analysis;
using Jfri.Core.Generation;
using Jfri.Core.Planning;

namespace Jfri.Core.Execution;

public sealed class DeploymentRequest
{
	public required DeploymentPlan Plan { get; init; }
	/// <summary>Writes a gzipped tarball of the source to deploy. Not needed for a rollback.</summary>
	public Func<Stream, CancellationToken, Task>? WriteSource { get; init; }
	/// <summary>Image tag and release id, e.g. "3f2a9c1b7d4e-20261121T101500".</summary>
	public required string Revision { get; init; }
	public required IReadOnlyDictionary<string, string> Secrets { get; init; }
	/// <summary>Public hostname the app is served on.</summary>
	public required string Host { get; init; }
	public string Branch { get; init; } = "main";
	/// <summary>SSH port the server's firewall must keep open.</summary>
	public int SshPort { get; init; } = 22;
}

public sealed class DeploymentFailedException(string step, string message) : Exception(message)
{
	public string Step { get; } = step;
}

/// <summary>
/// Runs a deployment as ordered, idempotent steps against an <see cref="IRemoteHost"/>: prepare
/// the server, upload the source and generated configuration, build and release, then check the
/// app answers over HTTPS. Secrets travel only inside uploaded 0600 files, and every log line is
/// scrubbed of secret values before it's recorded.
/// </summary>
public sealed class DeploymentEngine
{
	public const string GeneratedDockerfile = ".jfri/Dockerfile";

	public async Task<string> DeployAsync(DeploymentRequest request, IRemoteHost host, Action<string> log, CancellationToken ct)
	{
		var plan = request.Plan;
		var writeSource = request.WriteSource ?? throw new ArgumentException("A deployment needs the source.", nameof(request));
		Validate(request);
		var redact = Redactor(request.Secrets);
		void Log(string line) => log(redact(line));
		var context = new RenderContext(request.Host, request.Revision, request.Secrets);
		var appDir = ServerScripts.AppDir(plan.AppName);

		await Step("Preparing the server", async () =>
		{
			await Run(host, ServerScripts.Bootstrap(plan.Runtime, plan.AcmeEmail, request.SshPort), Log, "Preparing the server", ct);
		}, Log);

		await Step("Uploading the source", async () =>
		{
			var archive = Path.Combine(Path.GetTempPath(), $"jfri-src-{Guid.NewGuid():N}.tar.gz");
			try
			{
				await using (var file = File.Create(archive))
				{
					await writeSource(file, ct);
				}
				Log($"Source archive: {new FileInfo(archive).Length / 1024} KB");
				await using var read = File.OpenRead(archive);
				await host.UploadAsync($"{appDir}/source.tar.gz", read, "0600", ct);
			}
			finally
			{
				File.Delete(archive);
			}
		}, Log);

		var dockerfile = plan.App.BuildStrategy == BuildStrategy.ExistingDockerfile ? plan.App.DockerfilePath! : GeneratedDockerfile;
		await Step("Uploading the configuration", () => UploadConfiguration(host, plan, context, includeDockerfile: true, Log, ct), Log);

		await Step("Building and releasing", async () =>
		{
			await Run(host, ServerScripts.BuildAndDeploy(plan, request.Revision, dockerfile), Log, "Building and releasing", ct);
		}, Log);

		return await VerifyAsync(host, plan, request.Host, Log, ct);
	}

	/// <summary>
	/// Puts an earlier build back: checks its image is still on the server, renders the configuration
	/// for that revision, and runs the release. Nothing is uploaded or built, so it takes seconds, and
	/// nothing changes on the server if the image is gone. Database migrations aren't undone.
	/// </summary>
	public async Task<string> RollbackAsync(DeploymentRequest request, IRemoteHost host, Action<string> log, CancellationToken ct)
	{
		var plan = request.Plan;
		Validate(request);
		var redact = Redactor(request.Secrets);
		void Log(string line) => log(redact(line));
		var context = new RenderContext(request.Host, request.Revision, request.Secrets);

		await Step("Checking the earlier build is still on the server", async () =>
		{
			await Run(host, ServerScripts.CheckImage(plan, request.Revision), Log, "Checking the earlier build is still on the server", ct);
		}, Log);
		await Step("Uploading the configuration", () => UploadConfiguration(host, plan, context, includeDockerfile: false, Log, ct), Log);
		await Step("Releasing", async () =>
		{
			await Run(host, ServerScripts.Release(plan, request.Revision), Log, "Releasing", ct);
		}, Log);
		return await VerifyAsync(host, plan, request.Host, Log, ct);
	}

	/// <summary>Deletes an app from a server: containers, database, volumes, images and files. Backups are kept.</summary>
	public async Task TeardownAsync(string appName, IRemoteHost host, Action<string> log, CancellationToken ct)
	{
		if (!Planner.IsValidSlug(appName))
		{
			throw new ArgumentException($"Invalid app name {appName}.");
		}
		await Step($"Removing {appName} from the server", async () =>
		{
			await Run(host, ServerScripts.Teardown(appName), log, $"Removing {appName} from the server", ct);
		}, log);
	}

	/// <summary>
	/// The hostname an app is served on: its own domain, otherwise &lt;app&gt;.&lt;ip&gt;.sslip.io, which
	/// resolves to the server's IP so Let's Encrypt can issue for it before any DNS is set up, and stays
	/// unique per app on a shared server. A server added by hostname is served on that hostname.
	/// </summary>
	public static string PublicHost(string? domain, string serverHost, string appName) =>
		domain ?? (System.Net.IPAddress.TryParse(serverHost, out _) ? $"{appName}.{serverHost.Replace('.', '-').Replace(':', '-')}.sslip.io" : serverHost);

	static void Validate(DeploymentRequest request)
	{
		var plan = request.Plan;
		if (!Planner.IsValidSlug(plan.AppName))
		{
			throw new ArgumentException($"Invalid app name {plan.AppName}.");
		}
		if (!IsSafeToken(request.Revision) || !IsSafeHost(request.Host))
		{
			throw new ArgumentException("Invalid revision or host.");
		}
		// Paths and URLs that came from the repository end up in root shell scripts: allow plain characters only.
		foreach (var (what, value) in new[] { ("Dockerfile path", plan.App.DockerfilePath), ("build context", plan.BuildContext), ("project path", plan.App.EntryProject), ("document root", plan.App.DocumentRoot) })
		{
			if (!string.IsNullOrEmpty(value) && !IsSafeRelativePath(value))
			{
				throw new DeploymentFailedException("Planning", $"The {what} \"{value}\" contains characters Jfri won't put in a shell command. Rename it, or add a Dockerfile at the repository root.");
			}
		}
		if (plan.App.HealthCheckPath is { } health && !IsSafeUrlPath(health))
		{
			throw new DeploymentFailedException("Planning", $"The health check path \"{health}\" isn't a plain URL path.");
		}
	}

	static async Task UploadConfiguration(IRemoteHost host, DeploymentPlan plan, RenderContext context, bool includeDockerfile, Action<string> log, CancellationToken ct)
	{
		var appDir = ServerScripts.AppDir(plan.AppName);
		var runtimeDir = plan.Runtime == RuntimeKind.Kubernetes ? "k8s" : "compose";
		await Run(host, $"set -e\nrm -rf '{appDir}/overlay' '{appDir}/{runtimeDir}'\nmkdir -p '{appDir}/overlay/.jfri' '{appDir}/{runtimeDir}'\n", log, "Uploading the configuration", ct);
		if (includeDockerfile && plan.App.BuildStrategy == BuildStrategy.Generated)
		{
			await Upload(host, $"{appDir}/overlay/{GeneratedDockerfile}", DockerfileGenerator.Generate(plan), "0644", ct);
			await Upload(host, $"{appDir}/overlay/{GeneratedDockerfile}.dockerignore", DockerfileGenerator.DockerIgnore(plan), "0644", ct);
		}
		var artifacts = plan.Runtime == RuntimeKind.Kubernetes ? KubernetesGenerator.Generate(plan, context) : ComposeGenerator.Generate(plan, context);
		foreach (var artifact in artifacts)
		{
			await Upload(host, $"{appDir}/{artifact.Path}", artifact.Content, artifact.Mode, ct);
			log($"  {artifact.Path}{(artifact.Sensitive ? " (0600, contains secrets)" : "")}");
		}
	}

	static async Task<string> VerifyAsync(IRemoteHost host, DeploymentPlan plan, string publicHost, Action<string> log, CancellationToken ct)
	{
		await Step("Checking the app answers over HTTPS", async () =>
		{
			await Run(host, ServerScripts.Verify(plan, publicHost), log, "Checking the app answers over HTTPS", ct);
		}, log);
		var url = $"https://{publicHost}";
		log($"Live at {url}");
		return url;
	}

	static async Task Step(string title, Func<Task> action, Action<string> log)
	{
		log($"==> {title}");
		await action();
	}

	static async Task Run(IRemoteHost host, string script, Action<string> log, string step, CancellationToken ct)
	{
		var exit = await host.RunAsync(script, log, ct);
		if (exit != 0)
		{
			throw new DeploymentFailedException(step, $"{step} failed (exit code {exit}). See the log above.");
		}
	}

	static async Task Upload(IRemoteHost host, string path, string content, string mode, CancellationToken ct)
	{
		using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
		await host.UploadAsync(path, stream, mode, ct);
	}

	/// <summary>Replaces every secret value (longer than 5 chars) with *** in log output.</summary>
	public static Func<string, string> Redactor(IReadOnlyDictionary<string, string> secrets)
	{
		var values = secrets.Values.Where(v => v.Length > 5).Distinct().OrderByDescending(v => v.Length).ToArray();
		return line =>
		{
			foreach (var value in values)
			{
				if (line.Contains(value, StringComparison.Ordinal))
				{
					line = line.Replace(value, "***", StringComparison.Ordinal);
				}
			}
			return line;
		};
	}

	public static bool IsSafeToken(string value) => value.Length is > 0 and <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

	public static bool IsSafeRelativePath(string value) =>
		value.Length <= 300 && !value.StartsWith('-') && !value.StartsWith('/') && !value.Contains("..", StringComparison.Ordinal)
		&& value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/');

	public static bool IsSafeUrlPath(string value) =>
		value.Length <= 200 && value.StartsWith('/') && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '~');

	public static bool IsSafeHost(string value) => value.Length is > 0 and <= 253 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') && !value.StartsWith('-');
}
