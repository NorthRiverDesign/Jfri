using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Data;

public sealed class JfriDb(DbContextOptions<JfriDb> options) : DbContext(options)
{
	public DbSet<AdminUser> Admins => Set<AdminUser>();
	public DbSet<Connection> Connections => Set<Connection>();
	public DbSet<Server> Servers => Set<Server>();
	public DbSet<App> Apps => Set<App>();
	public DbSet<Deployment> Deployments => Set<Deployment>();

	protected override void OnModelCreating(ModelBuilder model)
	{
		model.Entity<AdminUser>().HasIndex(a => a.UserName).IsUnique();
		model.Entity<App>().HasIndex(a => a.Name).IsUnique();
		model.Entity<Deployment>().HasIndex(d => new { d.AppId, d.CreatedAt });
		model.Entity<Deployment>().HasOne<App>().WithMany().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
		model.Entity<App>().HasOne<Server>().WithMany().HasForeignKey(a => a.ServerId).OnDelete(DeleteBehavior.SetNull);
		model.Entity<Server>().HasOne<Connection>().WithMany().HasForeignKey(s => s.ConnectionId).OnDelete(DeleteBehavior.SetNull);
	}
}

public sealed class AdminUser
{
	public int Id { get; set; }
	public required string UserName { get; set; }
	public required string PasswordHash { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A hosting-provider API token. The token is encrypted at rest and never rendered back.</summary>
public sealed class Connection
{
	public int Id { get; set; }
	/// <summary>Provider id: hetzner, digitalocean, vultr, linode.</summary>
	public required string Provider { get; set; }
	public required string Name { get; set; }
	public required string EncryptedToken { get; set; }
	/// <summary>Last four characters, to tell tokens apart.</summary>
	public required string Hint { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class ServerStatus
{
	public const string Provisioning = "provisioning";
	public const string Ready = "ready";
	public const string Failed = "failed";
}

/// <summary>A server Jfri deploys to: one it created through a provider, or one the user brought (SSH).</summary>
public sealed class Server
{
	public int Id { get; set; }
	public required string Name { get; set; }
	/// <summary>"ssh" for a user's own server, otherwise the provider id.</summary>
	public required string Provider { get; set; }
	public int? ConnectionId { get; set; }
	public string? ProviderServerId { get; set; }
	public string? Region { get; set; }
	public string? Size { get; set; }
	/// <summary>IP address or hostname; empty while a new server is being created.</summary>
	public string Host { get; set; } = "";
	public int Port { get; set; } = 22;
	public string User { get; set; } = "root";
	public required string EncryptedPrivateKey { get; set; }
	public required string PublicKey { get; set; }
	/// <summary>Pinned SSH host key(s), learned on first connection.</summary>
	public string? KnownHosts { get; set; }
	/// <summary>Runtime installed on the server (Kubernetes or Compose); null until first deploy.</summary>
	public string? Runtime { get; set; }
	public string Status { get; set; } = ServerStatus.Ready;
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class App
{
	public int Id { get; set; }
	/// <summary>DNS-safe slug, also the Kubernetes namespace / Compose project.</summary>
	public required string Name { get; set; }
	public required string RepoUrl { get; set; }
	public string? Branch { get; set; }
	public string? EncryptedGitToken { get; set; }
	public string? Commit { get; set; }
	/// <summary>Serialized <see cref="Jfri.Core.Analysis.RepositoryProfile"/>.</summary>
	public required string ProfileJson { get; set; }
	/// <summary>Serialized <see cref="Jfri.Core.Planning.PlanOptions"/>.</summary>
	public required string OptionsJson { get; set; }
	/// <summary>Generated secrets (database passwords, app keys), encrypted. Kept across deploys.</summary>
	public string? EncryptedSecrets { get; set; }
	public required string EncryptedWebhookSecret { get; set; }
	public int? ServerId { get; set; }
	public string? LiveUrl { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}

public static class DeploymentStatus
{
	public const string Queued = "queued";
	public const string Running = "running";
	public const string Succeeded = "succeeded";
	public const string Failed = "failed";
}

public static class DeploymentKind
{
	/// <summary>Fetch, build and release the branch head.</summary>
	public const string Deploy = "deploy";
	/// <summary>Release an earlier build that's still on the server (no fetch, no build).</summary>
	public const string Rollback = "rollback";
	/// <summary>Delete the app from its server (containers, database, volumes; backups are kept).</summary>
	public const string Teardown = "teardown";
}

public sealed class Deployment
{
	public int Id { get; set; }
	public int AppId { get; set; }
	public int? ServerId { get; set; }
	public string Status { get; set; } = DeploymentStatus.Queued;
	/// <summary>What this run does: see <see cref="DeploymentKind"/>.</summary>
	public string Kind { get; set; } = DeploymentKind.Deploy;
	/// <summary>manual or webhook.</summary>
	public string Trigger { get; set; } = "manual";
	/// <summary>When creating a new server: provider, connection, region, size, backups (JSON).</summary>
	public string? NewServerJson { get; set; }
	public string? Commit { get; set; }
	/// <summary>Image tag and release id. Set when queued for a rollback (the build to put back).</summary>
	public string? Revision { get; set; }
	/// <summary>
	/// Serialized <see cref="Jfri.Core.Planning.DeploymentPlan"/> this revision was released with, so a
	/// rollback pairs the old image with the processes, port and settings it was built for.
	/// </summary>
	public string? PlanJson { get; set; }
	public string? Url { get; set; }
	public string? Error { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime? StartedAt { get; set; }
	public DateTime? FinishedAt { get; set; }
}

/// <summary>What to create when a deployment provisions a new server.</summary>
public sealed record NewServerSpec(string Provider, int ConnectionId, string Region, string Size, bool Backups);
