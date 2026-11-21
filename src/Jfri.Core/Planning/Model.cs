using System.Text.Json.Serialization;
using Jfri.Core.Analysis;

namespace Jfri.Core.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeKind>))]
public enum RuntimeKind
{
	/// <summary>Single-node k3s: Traefik ingress with Let's Encrypt, StatefulSet databases, CronJob backups.</summary>
	Kubernetes,
	/// <summary>Docker Compose behind Caddy: the lightest production-grade setup for one server.</summary>
	Compose,
}

[JsonConverter(typeof(JsonStringEnumConverter<SecretKind>))]
public enum SecretKind
{
	/// <summary>32 random URL-safe characters.</summary>
	Password,
	/// <summary>Laravel APP_KEY: "base64:" + 32 random bytes.</summary>
	LaravelKey,
	/// <summary>128 hex characters (Rails SECRET_KEY_BASE and similar).</summary>
	Hex64,
	/// <summary>50 random characters (Django SECRET_KEY).</summary>
	Long,
	/// <summary>Provided by the user (never generated).</summary>
	UserProvided,
}

/// <summary>Choices the user can override before deploying. Everything has a sensible default.</summary>
public sealed class PlanOptions
{
	public RuntimeKind Runtime { get; set; } = RuntimeKind.Kubernetes;
	/// <summary>Public hostname. When empty Jfri uses &lt;server-ip&gt;.sslip.io so HTTPS works before DNS is set up.</summary>
	public string? Domain { get; set; }
	/// <summary>Contact e-mail for Let's Encrypt.</summary>
	public string? AcmeEmail { get; set; }
	public int? WebReplicas { get; set; }
	public int DatabaseStorageGb { get; set; } = 10;
	public int VolumeStorageGb { get; set; } = 5;
	public bool Backups { get; set; } = true;
	public int BackupRetentionDays { get; set; } = 7;
	/// <summary>Turn on the hosting provider's whole-server backups (snapshots) as well.</summary>
	public bool ProviderBackups { get; set; } = true;
	/// <summary>Extra environment variables (non-secret) the user wants set.</summary>
	public Dictionary<string, string> ExtraEnvironment { get; set; } = [];
}

/// <summary>A process (container) the app runs: the web server, a queue worker, the scheduler.</summary>
public sealed class PlannedProcess
{
	public required string Name { get; init; }
	public required ProcessKind Kind { get; init; }
	/// <summary>Command override; null runs the image's default command.</summary>
	public string? Command { get; init; }
	public int Replicas { get; set; } = 1;
	public required Resources Resources { get; init; }
}

/// <summary>CPU in millicores, memory in MiB.</summary>
public sealed record Resources(int CpuRequest, int MemoryRequest, int CpuLimit, int MemoryLimit);

/// <summary>A backing service Jfri runs next to the app (database, cache).</summary>
public sealed class PlannedService
{
	public required ServiceKind Kind { get; init; }
	/// <summary>Service (and DNS) name inside the deployment: "db" or "redis".</summary>
	public required string Name { get; init; }
	public required string Image { get; init; }
	public required string Version { get; init; }
	public required int Port { get; init; }
	public int StorageGb { get; init; }
	public required Resources Resources { get; init; }
	public bool IsDatabase => Kind is not ServiceKind.Redis;
	/// <summary>Path inside the service container where its data lives.</summary>
	public string DataPath => Kind switch
	{
		ServiceKind.PostgreSql => VersionRange.ParseVersion(Version).Major >= 18 ? "/var/lib/postgresql" : "/var/lib/postgresql/data",
		ServiceKind.MySql or ServiceKind.MariaDb => "/var/lib/mysql",
		ServiceKind.MongoDb => "/data/db",
		ServiceKind.SqlServer => "/var/opt/mssql",
		ServiceKind.Redis => "/data",
		_ => "/data",
	};
}

/// <summary>A persistent volume mounted into the app's containers.</summary>
public sealed record PlannedVolume(string Name, string MountPath, int SizeGb, string Reason);

/// <summary>
/// An environment variable. <see cref="Value"/> may contain {{secret:NAME}} tokens; any variable
/// that does is treated as sensitive and only ever written to 0600 files / Kubernetes Secrets.
/// </summary>
public sealed record PlannedEnv(string Key, string Value)
{
	public bool IsSensitive => Value.Contains("{{secret:", StringComparison.Ordinal);
}

public sealed record PlannedSecret(string Name, SecretKind Kind, string Purpose);

public sealed class BackupPlan
{
	public bool Enabled { get; set; }
	/// <summary>Cron schedule (server time, UTC).</summary>
	public string Schedule { get; set; } = "15 3 * * *";
	public int RetentionDays { get; set; } = 7;
	public bool ProviderBackups { get; set; }
}

/// <summary>What the server needs: drives the size recommendation and the cost estimate.</summary>
public sealed record ResourceEstimate(double VCpu, int MemoryMb, int DiskGb, int MinimumMemoryMb)
{
	public override string ToString() => $"{VCpu:0.#} vCPU, {MemoryMb / 1024.0:0.#} GB RAM, {DiskGb} GB disk";
}

/// <summary>
/// The deployment Jfri proposes for a repository. Serializable, shown to the user to confirm or
/// override, and the single input every artifact generator renders from.
/// </summary>
public sealed class DeploymentPlan
{
	public required string AppName { get; init; }
	public required RuntimeKind Runtime { get; init; }
	public required AppProfile App { get; init; }
	/// <summary>Build context relative to the repo root ("" = root).</summary>
	public string BuildContext { get; init; } = "";
	public List<PlannedProcess> Processes { get; init; } = [];
	public string? ReleaseCommand { get; init; }
	public List<PlannedService> Services { get; init; } = [];
	public List<PlannedVolume> Volumes { get; init; } = [];
	public List<PlannedEnv> Environment { get; init; } = [];
	public List<PlannedSecret> Secrets { get; init; } = [];
	/// <summary>Repo-declared settings Jfri doesn't set (shown so the user can add them).</summary>
	public List<string> UnsetEnvironment { get; init; } = [];
	public string? Domain { get; init; }
	public string? AcmeEmail { get; init; }
	public required BackupPlan Backups { get; init; }
	public required ResourceEstimate Resources { get; init; }
	/// <summary>Why Jfri chose what it chose, in plain language.</summary>
	public List<string> Decisions { get; init; } = [];
	public List<string> Warnings { get; init; } = [];

	public string Namespace => AppName;
	public string ImageRepository => $"jfri/{AppName}";
	public PlannedProcess? Web => Processes.FirstOrDefault(p => p.Kind == ProcessKind.Web);
	public PlannedService? Database => Services.FirstOrDefault(s => s.IsDatabase);
	public PlannedService? Cache => Services.FirstOrDefault(s => s.Kind == ServiceKind.Redis);
}
