using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jfri.Core.Analysis.Detectors;

/// <summary>
/// Detects PHP apps (Laravel, Symfony, WordPress, plain PHP). Laravel gets the full treatment:
/// queue worker, scheduler, persistent storage, migrations on release and Vite asset builds,
/// all inferred from the repo's own config and .env.example.
/// </summary>
internal sealed partial class PhpDetector : IStackDetector
{
	public IEnumerable<Candidate> Detect(RepoFiles files)
	{
		foreach (var composerPath in files.FindByName("composer.json"))
		{
			var dir = RepoFiles.DirectoryOf(composerPath);
			if (dir.Split('/').Length > 3)
			{
				continue;
			}
			using var composer = Parsing.Json(files.Read(composerPath));
			if (composer is null)
			{
				continue;
			}
			var root = composer.RootElement;
			var require = Parsing.KeysOf(root, "require");
			var type = root.GetString("type");
			if (type is "library" or "composer-plugin" && !files.Exists(RepoFiles.Join(dir, "artisan")))
			{
				continue;
			}
			yield return Build(files, dir, composerPath, root, require);
		}
		// WordPress without composer.
		var wpConfig = files.FirstExisting("wp-config-sample.php", "wp-config.php");
		if (wpConfig is not null && !files.Exists("composer.json"))
		{
			var app = NewApp("", "WordPress", RuntimeDefaults.Php);
			app.DocumentRoot = "";
			app.AddEvidence(wpConfig, "WordPress config");
			var c = new Candidate(app) { Score = 85 };
			WordPressNeeds(c, wpConfig, "wp-content");
			yield return c;
		}
	}

	static Candidate Build(RepoFiles files, string dir, string composerPath, JsonElement composer, HashSet<string> require)
	{
		var phpConstraint = composer.GetObject("require")?.GetString("php");
		var platformPhp = composer.GetString("config", "platform", "php");
		var version = VersionRange.MajorMinor(platformPhp)
			?? VersionRange.Pick(phpConstraint, RuntimeDefaults.Php, RuntimeDefaults.PhpSupported)
			?? RuntimeDefaults.Php;
		var isLaravel = require.Contains("laravel/framework") || files.Exists(RepoFiles.Join(dir, "artisan"));
		var isSymfony = !isLaravel && (require.Contains("symfony/framework-bundle") || files.Exists(RepoFiles.Join(dir, "bin/console")));
		var isWordPress = require.Contains("johnpbloch/wordpress") || require.Contains("roots/wordpress") || require.Contains("roots/bedrock-autoloader");
		var framework = isLaravel ? "Laravel" : isSymfony ? "Symfony" : isWordPress ? "WordPress" : "PHP";
		var app = NewApp(dir, framework, version);
		app.AddEvidence(composerPath, phpConstraint is null ? "composer.json" : $"requires php {phpConstraint}");
		var candidate = new Candidate(app) { Score = isLaravel || isSymfony ? 95 : isWordPress ? 85 : 70 };
		candidate.Score -= dir.Count(c => c == '/') * 5;

		app.DocumentRoot = files.Exists(RepoFiles.Join(dir, "public/index.php")) || isLaravel || isSymfony ? "public"
			: files.Exists(RepoFiles.Join(dir, "web/index.php")) ? "web"
			: "";
		if (files.Exists(RepoFiles.Join(dir, "composer.lock")))
		{
			app.AddEvidence(RepoFiles.Join(dir, "composer.lock"), "locked dependencies");
		}
		var requireDev = Parsing.KeysOf(composer, "require-dev");
		if (requireDev.Contains("phpunit/phpunit") || requireDev.Contains("pestphp/pest"))
		{
			app.TestCommand = isLaravel ? "php artisan test" : "vendor/bin/phpunit";
		}

		// Front-end assets (Vite / Mix) built during the image build.
		var packageJson = RepoFiles.Join(dir, "package.json");
		using (var pkg = Parsing.Json(files.Read(packageJson)))
		{
			if (pkg is not null && pkg.RootElement.GetObject("scripts")?.GetString("build") is not null)
			{
				app.AssetBuildCommand = "npm run build";
				app.AddEvidence(packageJson, "front-end assets built with \"npm run build\"");
			}
		}

		var envSource = files.FirstExisting(RepoFiles.Join(dir, ".env.example"), RepoFiles.Join(dir, ".env.dist"), RepoFiles.Join(dir, ".env")) ?? "";
		var env = Parsing.ParseEnv(envSource.Length > 0 ? files.Read(envSource) : null);
		foreach (var (key, value) in env)
		{
			var secret = Parsing.LooksSecret(key);
			candidate.Needs.AddEnvironment(new EnvironmentKey(key, secret ? null : value, secret, envSource));
		}

		if (isLaravel)
		{
			Laravel(files, dir, candidate, env, envSource, require);
		}
		else if (isSymfony)
		{
			Symfony(files, dir, candidate, env, envSource, require);
		}
		else if (isWordPress)
		{
			WordPressNeeds(candidate, composerPath, files.DirectoryExists(RepoFiles.Join(dir, "web/app")) ? "web/app" : "wp-content");
		}
		RequireFromComposer(candidate, composerPath, require);
		return candidate;
	}

	static void Laravel(RepoFiles files, string dir, Candidate c, List<KeyValuePair<string, string>> env, string envSource, HashSet<string> require)
	{
		var app = c.App;
		var bootstrapPath = RepoFiles.Join(dir, "bootstrap/app.php");
		var bootstrap = files.Read(bootstrapPath);
		if (bootstrap is not null && HealthRegex().Match(bootstrap) is { Success: true } health)
		{
			app.HealthCheckPath = health.Groups[1].Value;
			app.AddEvidence(bootstrapPath, $"health route {app.HealthCheckPath}");
		}
		if (files.DirectoryExists(RepoFiles.Join(dir, "database/migrations")))
		{
			app.ReleaseCommand = "php artisan migrate --force";
			app.AddEvidence(RepoFiles.Join(dir, "database/migrations"), "database migrations run on each release");
		}

		var connection = Parsing.EnvValue(env, "DB_CONNECTION");
		if (connection is not null)
		{
			var evidence = new Evidence(envSource, $"DB_CONNECTION={connection}");
			ServiceKind? kind = connection switch
			{
				"pgsql" => ServiceKind.PostgreSql,
				"mysql" => ServiceKind.MySql,
				"mariadb" => ServiceKind.MariaDb,
				"sqlite" => ServiceKind.Sqlite,
				"sqlsrv" => ServiceKind.SqlServer,
				_ => null,
			};
			if (kind is not null)
			{
				c.Needs.Require(kind.Value, evidence, Confidence.High);
			}
		}
		else
		{
			// Laravel 11+ defaults to SQLite when DB_CONNECTION isn't set.
			c.Needs.Require(ServiceKind.Sqlite, new Evidence(envSource.Length > 0 ? envSource : "config/database.php", "Laravel default connection"), Confidence.Low);
		}

		var redisKey = new[] { "QUEUE_CONNECTION", "CACHE_STORE", "CACHE_DRIVER", "SESSION_DRIVER", "BROADCAST_CONNECTION" }
			.FirstOrDefault(k => Parsing.EnvValue(env, k) == "redis");
		if (redisKey is not null)
		{
			c.Needs.Require(ServiceKind.Redis, new Evidence(envSource, $"{redisKey}=redis"), Confidence.High);
		}
		else if (require.Contains("laravel/horizon"))
		{
			c.Needs.Require(ServiceKind.Redis, new Evidence(RepoFiles.Join(dir, "composer.json"), "requires laravel/horizon"), Confidence.High);
		}

		var queue = Parsing.EnvValue(env, "QUEUE_CONNECTION") ?? Parsing.EnvValue(env, "QUEUE_DRIVER");
		if (require.Contains("laravel/horizon"))
		{
			app.Processes.Add(new ProcessSpec("worker", ProcessKind.Worker, "php artisan horizon"));
			app.AddEvidence(RepoFiles.Join(dir, "composer.json"), "Horizon manages queue workers");
		}
		else if (queue is not null && queue != "sync" && queue != "null")
		{
			app.Processes.Add(new ProcessSpec("worker", ProcessKind.Worker, "php artisan queue:work --tries=3 --max-time=3600"));
			app.AddEvidence(envSource, $"QUEUE_CONNECTION={queue} needs a queue worker");
		}

		var consolePath = RepoFiles.Join(dir, "routes/console.php");
		var kernelPath = RepoFiles.Join(dir, "app/Console/Kernel.php");
		var console = files.Read(consolePath) ?? "";
		var kernel = files.Read(kernelPath) ?? "";
		if (console.Contains("Schedule::", StringComparison.Ordinal) || ScheduleRegex().IsMatch(kernel) || (bootstrap?.Contains("withSchedule", StringComparison.Ordinal) ?? false))
		{
			app.Processes.Add(new ProcessSpec("scheduler", ProcessKind.Scheduler, "php artisan schedule:work"));
			app.AddEvidence(console.Contains("Schedule::", StringComparison.Ordinal) ? consolePath : kernelPath, "scheduled tasks need the scheduler");
		}

		c.Needs.AddPersistentPath("/var/www/html/storage/app", "Laravel file storage (uploads, local disk)", new Evidence(RepoFiles.Join(dir, "storage"), "Laravel storage directory"));
		c.Needs.AddEnvironment(new EnvironmentKey("APP_KEY", null, true, envSource));
	}

	static void Symfony(RepoFiles files, string dir, Candidate c, List<KeyValuePair<string, string>> env, string envSource, HashSet<string> require)
	{
		var dbUrl = Parsing.EnvValue(env, "DATABASE_URL");
		if (dbUrl is not null)
		{
			var evidence = new Evidence(envSource, "DATABASE_URL");
			if (dbUrl.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
			{
				c.Needs.Require(ServiceKind.PostgreSql, evidence, Confidence.High, VersionFromUrl(dbUrl));
			}
			else if (dbUrl.StartsWith("mysql", StringComparison.OrdinalIgnoreCase))
			{
				c.Needs.Require(dbUrl.Contains("mariadb", StringComparison.OrdinalIgnoreCase) ? ServiceKind.MariaDb : ServiceKind.MySql, evidence, Confidence.High, VersionFromUrl(dbUrl));
			}
			else if (dbUrl.StartsWith("sqlite", StringComparison.OrdinalIgnoreCase))
			{
				c.Needs.Require(ServiceKind.Sqlite, evidence, Confidence.High);
			}
		}
		if (require.Contains("doctrine/doctrine-migrations-bundle") && files.DirectoryExists(RepoFiles.Join(dir, "migrations")))
		{
			c.App.ReleaseCommand = "php bin/console doctrine:migrations:migrate --no-interaction --allow-no-migration";
			c.App.AddEvidence(RepoFiles.Join(dir, "migrations"), "Doctrine migrations run on each release");
		}
		if (require.Contains("symfony/messenger"))
		{
			c.App.Processes.Add(new ProcessSpec("worker", ProcessKind.Worker, "php bin/console messenger:consume async --time-limit=3600"));
		}
		c.Needs.AddEnvironment(new EnvironmentKey("APP_SECRET", null, true, envSource));
	}

	static void WordPressNeeds(Candidate c, string evidenceFile, string contentDir)
	{
		c.Needs.Require(ServiceKind.MySql, new Evidence(evidenceFile, "WordPress requires MySQL"), Confidence.High);
		c.Needs.AddPersistentPath($"/var/www/html/{contentDir}/uploads", "WordPress media uploads", new Evidence(evidenceFile, "WordPress"));
	}

	static void RequireFromComposer(Candidate c, string composerPath, HashSet<string> require)
	{
		if (require.Contains("mongodb/laravel-mongodb") || require.Contains("mongodb/mongodb") || require.Contains("jenssegers/mongodb"))
		{
			c.Needs.Require(ServiceKind.MongoDb, new Evidence(composerPath, "requires a MongoDB client"), Confidence.High);
		}
		if (require.Contains("predis/predis") && !c.Needs.Needs(ServiceKind.Redis))
		{
			c.Needs.Require(ServiceKind.Redis, new Evidence(composerPath, "requires predis/predis"), Confidence.Medium);
		}
	}

	static AppProfile NewApp(string dir, string framework, string version)
	{
		var app = new AppProfile
		{
			Stack = Stack.Php,
			Framework = framework,
			RuntimeVersion = version,
			RootPath = dir,
			PackageManager = "composer",
			BuildCommand = "composer install --no-dev --optimize-autoloader --no-interaction",
			Port = 8080,
			Confidence = Confidence.High,
		};
		app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
		return app;
	}

	static string? VersionFromUrl(string url)
	{
		var m = ServerVersionRegex().Match(url);
		return m.Success ? VersionRange.Major(m.Groups[1].Value) : null;
	}

	[GeneratedRegex(@"health:\s*['""](/[A-Za-z0-9/._~-]*)['""]")]
	private static partial Regex HealthRegex();

	[GeneratedRegex(@"\$schedule->")]
	private static partial Regex ScheduleRegex();

	[GeneratedRegex(@"serverVersion=([\d.]+)")]
	private static partial Regex ServerVersionRegex();
}
