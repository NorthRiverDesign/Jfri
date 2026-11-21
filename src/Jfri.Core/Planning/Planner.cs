using System.Text.RegularExpressions;
using Jfri.Core.Analysis;

namespace Jfri.Core.Planning;

/// <summary>
/// Turns what the analyzer found into a production deployment plan. This is where established
/// best practice lives: private databases with generated credentials, persistent volumes,
/// daily backups with retention, a release step for migrations, sized resources, HTTPS.
/// </summary>
public static partial class Planner
{
	public const string DbName = "app";
	public const string DbUser = "app";

	public static DeploymentPlan Plan(RepositoryProfile profile, PlanOptions? options = null, string? appName = null)
	{
		options ??= new PlanOptions();
		var app = profile.App ?? throw new InvalidOperationException("Nothing deployable was found in this repository.");
		var name = Slug(appName ?? profile.RepositoryName ?? "app");
		var decisions = new List<string>();
		var warnings = new List<string>(profile.Warnings);

		decisions.Add(options.Runtime == RuntimeKind.Kubernetes
			? "Runtime: single-node Kubernetes (k3s) with Traefik and automatic Let's Encrypt certificates."
			: "Runtime: Docker Compose behind Caddy with automatic HTTPS. The lightest production setup for one server.");
		decisions.Add(app.BuildStrategy == BuildStrategy.ExistingDockerfile
			? $"Build: the repository's own {app.DockerfilePath} (an existing Dockerfile always wins over inference)."
			: $"Build: a generated multi-stage Dockerfile for {app.Framework ?? app.Stack.ToString()} {app.RuntimeVersion}, running as a non-root user.");

		// Services
		var services = new List<PlannedService>();
		foreach (var requirement in profile.Services.Where(s => s.Kind != ServiceKind.Sqlite))
		{
			var service = PlanService(requirement, options);
			services.Add(service);
			decisions.Add(service.IsDatabase
				? $"Database: {Describe(service)} on a {service.StorageGb} GB persistent volume, reachable only from the app, with a generated password. Evidence: {requirement.Evidence[0]}."
				: $"Cache/queue: {Describe(service)}, private, password-protected. Evidence: {requirement.Evidence[0]}.");
			if (service.Kind == ServiceKind.SqlServer)
			{
				warnings.Add("SQL Server needs an x86-64 server with at least 2 GB RAM for itself, and runs as Express edition. Consider PostgreSQL for new apps.");
			}
		}
		if (profile.Needs(ServiceKind.Sqlite))
		{
			decisions.Add("Database: SQLite, stored on a persistent volume. SQLite apps run a single web replica.");
		}

		// Processes
		var hasLocalState = profile.PersistentPaths.Count > 0;
		var webReplicas = options.WebReplicas ?? (options.Runtime == RuntimeKind.Kubernetes && !profile.Needs(ServiceKind.Sqlite) ? 2 : 1);
		if (profile.Needs(ServiceKind.Sqlite))
		{
			webReplicas = 1;
		}
		var processes = new List<PlannedProcess>();
		foreach (var spec in app.Processes.Count > 0 ? app.Processes : [new ProcessSpec("web", ProcessKind.Web, null)])
		{
			processes.Add(new PlannedProcess
			{
				Name = spec.Name,
				Kind = spec.Kind,
				Command = spec.Command,
				Replicas = spec.Kind == ProcessKind.Web ? webReplicas : 1,
				Resources = ProcessResources(app.Stack, spec.Kind),
			});
			if (spec.Kind != ProcessKind.Web)
			{
				decisions.Add($"Process \"{spec.Name}\": {spec.Command ?? "image default"} ({(spec.Kind == ProcessKind.Scheduler ? "exactly one instance" : "restarted automatically")}).");
			}
		}
		if (webReplicas > 1)
		{
			decisions.Add($"Web: {webReplicas} replicas with rolling updates and a disruption budget, so deploys don't drop requests.");
		}
		if (app.ReleaseCommand is not null)
		{
			decisions.Add($"Release step: \"{app.ReleaseCommand}\" runs once per deploy, before new code takes traffic.");
		}

		// Volumes
		var volumes = new List<PlannedVolume>();
		foreach (var path in profile.PersistentPaths)
		{
			var volumeName = VolumeName(path.ContainerPath, volumes);
			var size = path.ContainerPath.Contains("DataProtection", StringComparison.Ordinal) ? 1 : options.VolumeStorageGb;
			volumes.Add(new PlannedVolume(volumeName, path.ContainerPath, size, path.Reason));
			decisions.Add($"Volume \"{volumeName}\" at {path.ContainerPath}: {path.Reason}.");
		}
		if (hasLocalState && options.Runtime == RuntimeKind.Kubernetes && webReplicas > 1)
		{
			decisions.Add("Replicas share their volumes on the single node. To scale across nodes, move uploads to object storage.");
		}

		// Environment and secrets
		var secrets = new List<PlannedSecret>();
		var env = new List<PlannedEnv>();
		WireEnvironment(profile, app, services, env, secrets);
		foreach (var (key, value) in options.ExtraEnvironment)
		{
			env.RemoveAll(e => e.Key == key);
			env.Add(new PlannedEnv(key, value));
		}
		var unset = profile.Environment.Select(e => e.Key).Where(k => env.All(e => e.Key != k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

		// Backups
		var backups = new BackupPlan
		{
			Enabled = options.Backups && (services.Any(s => s.IsDatabase) || profile.Needs(ServiceKind.Sqlite)),
			RetentionDays = Math.Clamp(options.BackupRetentionDays, 1, 90),
			ProviderBackups = options.ProviderBackups,
		};
		if (backups.Enabled)
		{
			decisions.Add($"Backups: nightly database dump at 03:15 UTC kept for {backups.RetentionDays} days on the server{(backups.ProviderBackups ? ", plus the provider's whole-server backups for off-machine copies" : "")}.");
		}
		else if (services.Any(s => s.IsDatabase))
		{
			warnings.Add("Backups are turned off. A failed disk would lose the database.");
		}
		if (services.Any(s => s.IsDatabase) && !options.ProviderBackups)
		{
			warnings.Add("Database dumps stay on the same server. Turn on provider backups, or copy /var/lib/jfri/backups off the machine.");
		}

		if (app.Stack == Stack.Php && app.Framework == "Laravel")
		{
			warnings.Add("Laravel behind a proxy: trust it (->withMiddleware(fn ($m) => $m->trustProxies(at: '*')) in bootstrap/app.php) so generated URLs use https.");
		}
		if (string.IsNullOrWhiteSpace(options.Domain))
		{
			decisions.Add("Address: <server-ip>.sslip.io until you point a domain at the server, so HTTPS works from the first deploy.");
		}

		var plan = new DeploymentPlan
		{
			AppName = name,
			Runtime = options.Runtime,
			App = app,
			BuildContext = BuildContextFor(app),
			Processes = processes,
			ReleaseCommand = app.ReleaseCommand is { } release && app.Framework == "Laravel" && profile.Needs(ServiceKind.Sqlite)
				? $"touch /var/www/html/database/data/database.sqlite && {release}"
				: app.ReleaseCommand,
			Services = services,
			Volumes = volumes,
			Environment = env,
			Secrets = secrets,
			UnsetEnvironment = unset,
			Domain = string.IsNullOrWhiteSpace(options.Domain) ? null : options.Domain.Trim().ToLowerInvariant(),
			AcmeEmail = string.IsNullOrWhiteSpace(options.AcmeEmail) ? null : options.AcmeEmail.Trim(),
			Backups = backups,
			Resources = Estimate(options.Runtime, processes, services, volumes),
			Decisions = decisions,
			Warnings = warnings,
		};
		return plan;
	}

	/// <summary>.NET builds from the repo root (multi-project restores); a root-level Dockerfile builds from the root.</summary>
	static string BuildContextFor(AppProfile app)
	{
		if (app.BuildStrategy == BuildStrategy.ExistingDockerfile)
		{
			return RepoFiles.DirectoryOf(app.DockerfilePath ?? "");
		}
		return app.Stack == Stack.DotNet ? "" : app.RootPath;
	}

	static PlannedService PlanService(ServiceRequirement requirement, PlanOptions options)
	{
		var (image, version, port) = requirement.Kind switch
		{
			ServiceKind.PostgreSql => ("postgres", Pick(requirement.Version, "17", 13, 18), 5432),
			ServiceKind.MySql => ("mysql", requirement.Version is "5" or "5.7" ? "8.0" : Pick(requirement.Version, "8.4", 8, 9), 3306),
			ServiceKind.MariaDb => ("mariadb", Pick(requirement.Version, "11.4", 10, 11), 3306),
			ServiceKind.MongoDb => ("mongo", Pick(requirement.Version, "8.0", 6, 8), 27017),
			ServiceKind.SqlServer => ("mcr.microsoft.com/mssql/server", "2022-latest", 1433),
			ServiceKind.Redis => ("valkey/valkey", "8", 6379),
			_ => throw new NotSupportedException(requirement.Kind.ToString()),
		};
		var tag = requirement.Kind switch
		{
			ServiceKind.PostgreSql or ServiceKind.Redis => $"{version}-alpine",
			_ => version,
		};
		return new PlannedService
		{
			Kind = requirement.Kind,
			Name = requirement.Kind == ServiceKind.Redis ? "redis" : "db",
			Image = $"{image}:{tag}",
			Version = version,
			Port = port,
			StorageGb = requirement.Kind == ServiceKind.Redis ? 1 : Math.Max(1, options.DatabaseStorageGb),
			Resources = requirement.Kind switch
			{
				ServiceKind.PostgreSql => new Resources(100, 256, 1000, 1024),
				ServiceKind.MySql or ServiceKind.MariaDb => new Resources(200, 512, 1000, 1024),
				ServiceKind.MongoDb => new Resources(200, 512, 1000, 1024),
				ServiceKind.SqlServer => new Resources(500, 2048, 2000, 3072),
				_ => new Resources(50, 64, 500, 256),
			},
		};
	}

	/// <summary>Keeps a repo-declared major version when it's in the supported range, else the default.</summary>
	static string Pick(string? declared, string fallback, int minMajor, int maxMajor)
	{
		if (declared is null)
		{
			return fallback;
		}
		var v = VersionRange.ParseVersion(declared);
		if (v.Major < minMajor || v.Major > maxMajor)
		{
			return fallback;
		}
		return declared.Contains('.') ? $"{v.Major}.{v.Minor}" : v.Major.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	static Resources ProcessResources(Stack stack, ProcessKind kind)
	{
		if (kind == ProcessKind.Scheduler)
		{
			return new Resources(50, 128, 500, 256);
		}
		return stack switch
		{
			Stack.Go => new Resources(50, 64, 1000, 256),
			Stack.Static => new Resources(10, 32, 250, 128),
			Stack.Ruby => new Resources(200, 384, 1000, 1024),
			Stack.Java => new Resources(250, 512, 2000, 1024),
			_ => new Resources(100, 192, 1000, 512),
		};
	}

	static ResourceEstimate Estimate(RuntimeKind runtime, List<PlannedProcess> processes, List<PlannedService> services, List<PlannedVolume> volumes)
	{
		// Size for typical usage (between request and limit), plus OS and platform overhead.
		var platformMb = runtime == RuntimeKind.Kubernetes ? 900 : 250;
		const int osMb = 350;
		var workloads = processes.Select(p => (p.Resources, p.Replicas)).Concat(services.Select(s => (s.Resources, Replicas: 1))).ToList();
		var typicalMb = workloads.Sum(w => (w.Resources.MemoryRequest + w.Resources.MemoryLimit) / 2 * w.Replicas);
		var minimumMb = workloads.Sum(w => w.Resources.MemoryRequest * w.Replicas) + platformMb + osMb;
		var memory = Math.Max(typicalMb + platformMb + osMb, runtime == RuntimeKind.Kubernetes ? 2048 : 1024);
		var cpu = Math.Max(runtime == RuntimeKind.Kubernetes ? 2 : 1, Math.Ceiling(workloads.Sum(w => w.Resources.CpuRequest * w.Replicas) / 1000.0 * 2));
		var disk = 25 // OS, container runtime, a few image revisions
			+ services.Sum(s => s.StorageGb) * 2 // data plus on-server backups
			+ volumes.Sum(v => v.SizeGb);
		return new ResourceEstimate(cpu, memory, disk, minimumMb);
	}

	static void WireEnvironment(RepositoryProfile profile, AppProfile app, List<PlannedService> services, List<PlannedEnv> env, List<PlannedSecret> secrets)
	{
		void Set(string key, string value)
		{
			env.RemoveAll(e => e.Key == key);
			env.Add(new PlannedEnv(key, value));
		}
		string Secret(string name, SecretKind kind, string purpose)
		{
			if (secrets.All(s => s.Name != name))
			{
				secrets.Add(new PlannedSecret(name, kind, purpose));
			}
			return $"{{{{secret:{name}}}}}";
		}
		const string url = "{{url}}";
		var host = "{{host}}";
		var db = services.FirstOrDefault(s => s.IsDatabase);
		var cache = services.FirstOrDefault(s => s.Kind == ServiceKind.Redis);
		string? dbPassword = db is null ? null : Secret("DB_PASSWORD", SecretKind.Password, "database password");
		string? redisPassword = cache is null ? null : Secret("REDIS_PASSWORD", SecretKind.Password, "cache password");
		if (db?.Kind is ServiceKind.MySql or ServiceKind.MariaDb)
		{
			Secret("DB_ROOT_PASSWORD", SecretKind.Password, "database root password");
		}
		var scheme = db?.Kind switch
		{
			ServiceKind.PostgreSql => "postgresql",
			ServiceKind.MySql => "mysql",
			ServiceKind.MariaDb => "mysql",
			ServiceKind.MongoDb => "mongodb",
			ServiceKind.SqlServer => "sqlserver",
			_ => null,
		};
		var dbUser = db?.Kind == ServiceKind.SqlServer ? "sa" : DbUser;
		var databaseUrl = db is null ? null
			: db.Kind == ServiceKind.MongoDb ? $"mongodb://{dbUser}:{dbPassword}@{db.Name}:{db.Port}/{DbName}?authSource=admin"
			: $"{scheme}://{dbUser}:{dbPassword}@{db.Name}:{db.Port}/{DbName}";
		var redisUrl = cache is null ? null : $"redis://:{redisPassword}@{cache.Name}:{cache.Port}/0";
		var sqlite = profile.Needs(ServiceKind.Sqlite);

		switch (app.Stack)
		{
			case Stack.Php when app.Framework == "Laravel":
				Set("APP_ENV", "production");
				Set("APP_DEBUG", "false");
				Set("APP_URL", url);
				Set("APP_KEY", Secret("APP_KEY", SecretKind.LaravelKey, "Laravel encryption key"));
				Set("LOG_CHANNEL", "stderr");
				Set("SESSION_SECURE_COOKIE", "true");
				if (db is not null)
				{
					Set("DB_CONNECTION", db.Kind switch { ServiceKind.PostgreSql => "pgsql", ServiceKind.MariaDb => "mariadb", ServiceKind.SqlServer => "sqlsrv", ServiceKind.MongoDb => "mongodb", _ => "mysql" });
					Set("DB_HOST", db.Name);
					Set("DB_PORT", db.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
					Set("DB_DATABASE", DbName);
					Set("DB_USERNAME", dbUser);
					Set("DB_PASSWORD", dbPassword!);
				}
				else if (sqlite)
				{
					Set("DB_CONNECTION", "sqlite");
					Set("DB_DATABASE", "/var/www/html/database/data/database.sqlite");
				}
				if (cache is not null)
				{
					Set("REDIS_HOST", cache.Name);
					Set("REDIS_PORT", "6379");
					Set("REDIS_PASSWORD", redisPassword!);
				}
				break;
			case Stack.Php when app.Framework == "Symfony":
				Set("APP_ENV", "prod");
				Set("APP_DEBUG", "0");
				Set("APP_SECRET", Secret("APP_SECRET", SecretKind.Hex64, "Symfony secret"));
				if (db is not null)
				{
					Set("DATABASE_URL", $"{databaseUrl}?serverVersion={db.Version}");
				}
				else if (sqlite)
				{
					Set("DATABASE_URL", "sqlite:////var/www/html/var/data/data.db");
				}
				break;
			case Stack.Php when app.Framework == "WordPress" && db is not null:
				Set("WORDPRESS_DB_HOST", db.Name);
				Set("WORDPRESS_DB_NAME", DbName);
				Set("WORDPRESS_DB_USER", dbUser);
				Set("WORDPRESS_DB_PASSWORD", dbPassword!);
				foreach (var salt in new[] { "AUTH_KEY", "SECURE_AUTH_KEY", "LOGGED_IN_KEY", "NONCE_KEY", "AUTH_SALT", "SECURE_AUTH_SALT", "LOGGED_IN_SALT", "NONCE_SALT" })
				{
					Set(salt, Secret(salt, SecretKind.Long, "WordPress salt"));
				}
				break;
			case Stack.DotNet:
				Set("ASPNETCORE_ENVIRONMENT", "Production");
				Set("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true");
				var connectionNames = profile.Environment.Where(e => e.Key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)).Select(e => e.Key).ToList();
				if (db is not null)
				{
					var cs = db.Kind switch
					{
						ServiceKind.PostgreSql => $"Host={db.Name};Port={db.Port};Database={DbName};Username={dbUser};Password={dbPassword}",
						ServiceKind.MySql or ServiceKind.MariaDb => $"Server={db.Name};Port={db.Port};Database={DbName};User={dbUser};Password={dbPassword}",
						ServiceKind.SqlServer => $"Server={db.Name},{db.Port};Database={DbName};User Id={dbUser};Password={dbPassword};TrustServerCertificate=True",
						_ => databaseUrl!,
					};
					var targets = connectionNames.Where(n => !n.Contains("Redis", StringComparison.OrdinalIgnoreCase)).ToList();
					if (targets.Count == 0)
					{
						targets.Add("ConnectionStrings__DefaultConnection");
					}
					foreach (var key in targets)
					{
						Set(key, cs);
					}
				}
				else if (sqlite)
				{
					foreach (var key in connectionNames.DefaultIfEmpty("ConnectionStrings__DefaultConnection"))
					{
						Set(key, "Data Source=/app/data/app.db");
					}
				}
				if (cache is not null)
				{
					var redisKey = connectionNames.FirstOrDefault(n => n.Contains("Redis", StringComparison.OrdinalIgnoreCase)) ?? "ConnectionStrings__Redis";
					Set(redisKey, $"{cache.Name}:{cache.Port},password={redisPassword}");
				}
				break;
			case Stack.Python:
				if (app.Framework == "Django")
				{
					Set("DJANGO_SECRET_KEY", Secret("DJANGO_SECRET_KEY", SecretKind.Long, "Django secret key"));
					Set("SECRET_KEY", "{{secret:DJANGO_SECRET_KEY}}");
					Set("DJANGO_DEBUG", "False");
					Set("DEBUG", "False");
					Set("DJANGO_ALLOWED_HOSTS", host);
					Set("ALLOWED_HOSTS", host);
					Set("CSRF_TRUSTED_ORIGINS", url);
				}
				if (databaseUrl is not null)
				{
					Set("DATABASE_URL", databaseUrl);
				}
				if (redisUrl is not null)
				{
					Set("REDIS_URL", redisUrl);
					Set("CELERY_BROKER_URL", redisUrl);
				}
				break;
			case Stack.Ruby:
				Set("RAILS_ENV", "production");
				Set("RACK_ENV", "production");
				Set("RAILS_LOG_TO_STDOUT", "1");
				Set("RAILS_SERVE_STATIC_FILES", "1");
				Set("SECRET_KEY_BASE", Secret("SECRET_KEY_BASE", SecretKind.Hex64, "Rails secret key base"));
				if (databaseUrl is not null)
				{
					Set("DATABASE_URL", databaseUrl.Replace("postgresql://", "postgres://", StringComparison.Ordinal).Replace("mysql://", "mysql2://", StringComparison.Ordinal));
				}
				if (redisUrl is not null)
				{
					Set("REDIS_URL", redisUrl);
				}
				break;
			case Stack.Java:
				if (db is not null && db.Kind is ServiceKind.PostgreSql or ServiceKind.MySql or ServiceKind.MariaDb)
				{
					var jdbc = db.Kind == ServiceKind.PostgreSql ? "postgresql" : db.Kind == ServiceKind.MariaDb ? "mariadb" : "mysql";
					Set("SPRING_DATASOURCE_URL", $"jdbc:{jdbc}://{db.Name}:{db.Port}/{DbName}");
					Set("SPRING_DATASOURCE_USERNAME", dbUser);
					Set("SPRING_DATASOURCE_PASSWORD", dbPassword!);
				}
				else if (db?.Kind == ServiceKind.MongoDb)
				{
					Set("SPRING_DATA_MONGODB_URI", databaseUrl!);
				}
				if (cache is not null)
				{
					Set("SPRING_DATA_REDIS_HOST", cache.Name);
					Set("SPRING_DATA_REDIS_PASSWORD", redisPassword!);
				}
				break;
			default:
				if (app.Stack == Stack.Node)
				{
					Set("NODE_ENV", "production");
				}
				if (databaseUrl is not null)
				{
					Set("DATABASE_URL", databaseUrl);
				}
				else if (sqlite)
				{
					Set("DATABASE_URL", "file:/app/data/app.db");
				}
				if (redisUrl is not null)
				{
					Set("REDIS_URL", redisUrl);
				}
				break;
		}
		if (app.Stack is not (Stack.Static or Stack.Php))
		{
			Set("PORT", app.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}
	}

	static string VolumeName(string containerPath, List<PlannedVolume> existing)
	{
		var baseName = containerPath switch
		{
			_ when containerPath.Contains("DataProtection", StringComparison.Ordinal) => "keys",
			_ when containerPath.EndsWith("/storage/app", StringComparison.Ordinal) => "storage",
			_ when containerPath.Contains("upload", StringComparison.OrdinalIgnoreCase) => "uploads",
			_ when containerPath.EndsWith("/media", StringComparison.Ordinal) => "media",
			_ => Slug(containerPath.TrimEnd('/').Split('/').Last()),
		};
		var name = baseName;
		for (var i = 2; existing.Any(v => v.Name == name); i++)
		{
			name = $"{baseName}{i}";
		}
		return name;
	}

	static string Describe(PlannedService service) => service.Kind switch
	{
		ServiceKind.PostgreSql => $"PostgreSQL {service.Version}",
		ServiceKind.MySql => $"MySQL {service.Version}",
		ServiceKind.MariaDb => $"MariaDB {service.Version}",
		ServiceKind.MongoDb => $"MongoDB {service.Version}",
		ServiceKind.SqlServer => "SQL Server 2022",
		ServiceKind.Redis => $"Valkey {service.Version} (open-source, Redis-compatible)",
		_ => service.Kind.ToString(),
	};

	/// <summary>Lowercase DNS-label-safe name: [a-z0-9-], 1-40 chars, starting with a letter.</summary>
	public static string Slug(string value)
	{
		var slug = SlugRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');
		if (slug.Length == 0)
		{
			return "app";
		}
		if (!char.IsAsciiLetter(slug[0]))
		{
			slug = "app-" + slug;
		}
		slug = slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
		return slug;
	}

	public static bool IsValidSlug(string value) => ValidSlugRegex().IsMatch(value);

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex SlugRegex();

	[GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
	private static partial Regex ValidSlugRegex();
}
