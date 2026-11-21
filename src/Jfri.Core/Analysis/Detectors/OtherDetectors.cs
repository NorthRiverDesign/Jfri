using System.Text.RegularExpressions;

namespace Jfri.Core.Analysis.Detectors;

/// <summary>Ruby on Rails (and Rack) apps.</summary>
internal sealed partial class RubyDetector : IStackDetector
{
	public IEnumerable<Candidate> Detect(RepoFiles files)
	{
		var gemfile = files.Read("Gemfile");
		if (gemfile is null)
		{
			yield break;
		}
		bool Has(string gem) => Regex.IsMatch(gemfile, $@"^\s*gem\s+['""]{Regex.Escape(gem)}['""]", RegexOptions.Multiline);
		var isRails = Has("rails") || files.Exists("config/application.rb");
		if (!isRails && !files.Exists("config.ru"))
		{
			yield break;
		}
		var version = VersionRange.MajorMinor(files.Read(".ruby-version")) ?? VersionRange.MajorMinor(RubyVersionRegex().Match(gemfile).Groups[1].Value) ?? RuntimeDefaults.Ruby;
		var app = new AppProfile
		{
			Stack = Stack.Ruby,
			Framework = isRails ? "Ruby on Rails" : "Rack",
			RuntimeVersion = version,
			PackageManager = "bundler",
			Port = 3000,
			StartCommand = isRails ? "bin/rails server -b 0.0.0.0 -p 3000" : "bundle exec rackup -o 0.0.0.0 -p 3000",
			Confidence = isRails ? Confidence.High : Confidence.Medium,
		};
		app.AddEvidence("Gemfile", isRails ? "depends on rails" : "Rack app (config.ru)");
		app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
		var candidate = new Candidate(app) { Score = isRails ? 90 : 60 };
		if (isRails)
		{
			app.ReleaseCommand = "bin/rails db:prepare";
			app.BuildCommand = "bin/rails assets:precompile";
			app.HealthCheckPath = (files.Read("config/routes.rb") ?? "").Contains("rails/health", StringComparison.Ordinal) ? "/up" : null;
			app.TestCommand = files.DirectoryExists("spec") ? "bundle exec rspec" : "bin/rails test";
			candidate.Needs.AddEnvironment(new EnvironmentKey("SECRET_KEY_BASE", null, true, "config/credentials"));
			candidate.Needs.AddPersistentPath("/rails/storage", "Active Storage local files", new Evidence("config/storage.yml", "Rails storage"));
		}
		var adapter = AdapterRegex().Match(files.Read("config/database.yml") ?? "");
		if (Has("pg") || adapter.Groups[1].Value is "postgresql")
		{
			candidate.Needs.Require(ServiceKind.PostgreSql, new Evidence("Gemfile", "pg gem"), Confidence.High);
		}
		else if (Has("mysql2") || Has("trilogy"))
		{
			candidate.Needs.Require(ServiceKind.MySql, new Evidence("Gemfile", "MySQL gem"), Confidence.High);
		}
		else if (Has("sqlite3"))
		{
			candidate.Needs.Require(ServiceKind.Sqlite, new Evidence("Gemfile", "sqlite3 gem"), Confidence.High);
		}
		if (Has("redis") || Has("sidekiq"))
		{
			candidate.Needs.Require(ServiceKind.Redis, new Evidence("Gemfile", Has("sidekiq") ? "Sidekiq needs Redis" : "redis gem"), Confidence.High);
		}
		if (Has("sidekiq"))
		{
			app.Processes.Add(new ProcessSpec("worker", ProcessKind.Worker, "bundle exec sidekiq"));
		}
		else if (Has("solid_queue"))
		{
			app.Processes.Add(new ProcessSpec("worker", ProcessKind.Worker, "bin/jobs"));
		}
		yield return candidate;
	}

	[GeneratedRegex(@"^\s*ruby\s+['""]([^'""]+)['""]", RegexOptions.Multiline)]
	private static partial Regex RubyVersionRegex();

	[GeneratedRegex(@"adapter:\s*(\w+)")]
	private static partial Regex AdapterRegex();
}

/// <summary>Go modules with a main package.</summary>
internal sealed partial class GoDetector : IStackDetector
{
	public IEnumerable<Candidate> Detect(RepoFiles files)
	{
		var gomod = files.Read("go.mod");
		if (gomod is null)
		{
			yield break;
		}
		var main = files.Exists("main.go") ? "." : files.FindByName("main.go").Where(f => f.StartsWith("cmd/", StringComparison.Ordinal)).Select(f => "./" + RepoFiles.DirectoryOf(f)).OrderBy(f => f.Length).FirstOrDefault();
		if (main is null)
		{
			yield break;
		}
		var app = new AppProfile
		{
			Stack = Stack.Go,
			Framework = "Go",
			RuntimeVersion = VersionRange.MajorMinor(GoVersionRegex().Match(gomod).Groups[1].Value) ?? RuntimeDefaults.Go,
			PackageManager = "go",
			BuildCommand = $"go build -trimpath -ldflags=\"-s -w\" -o /out/app {main}",
			StartCommand = "/app",
			Port = 8080,
			TestCommand = "go test ./...",
			Confidence = Confidence.High,
		};
		app.AddEvidence("go.mod", $"Go module, main package {main}");
		app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
		var candidate = new Candidate(app) { Score = 80 };
		foreach (var (module, kind) in new[] { ("github.com/jackc/pgx", ServiceKind.PostgreSql), ("github.com/lib/pq", ServiceKind.PostgreSql), ("github.com/go-sql-driver/mysql", ServiceKind.MySql), ("go.mongodb.org/mongo-driver", ServiceKind.MongoDb), ("github.com/redis/go-redis", ServiceKind.Redis), ("github.com/go-redis/redis", ServiceKind.Redis), ("modernc.org/sqlite", ServiceKind.Sqlite), ("github.com/mattn/go-sqlite3", ServiceKind.Sqlite) })
		{
			if (gomod.Contains(module, StringComparison.Ordinal))
			{
				candidate.Needs.Require(kind, new Evidence("go.mod", $"requires {module}"), Confidence.High);
			}
		}
		if (gomod.Contains("github.com/mattn/go-sqlite3", StringComparison.Ordinal))
		{
			candidate.Needs.Warnings.Add("mattn/go-sqlite3 needs cgo; the generated image builds with CGO_ENABLED=0, so switch to modernc.org/sqlite or adjust the Dockerfile.");
		}
		yield return candidate;
	}

	[GeneratedRegex(@"^go\s+([\d.]+)", RegexOptions.Multiline)]
	private static partial Regex GoVersionRegex();
}

/// <summary>Spring Boot apps built with Maven or Gradle.</summary>
internal sealed partial class JavaDetector : IStackDetector
{
	public IEnumerable<Candidate> Detect(RepoFiles files)
	{
		var buildFile = files.FirstExisting("pom.xml", "build.gradle.kts", "build.gradle");
		if (buildFile is null)
		{
			yield break;
		}
		var text = files.Read(buildFile) ?? "";
		if (!text.Contains("spring-boot", StringComparison.Ordinal) && !text.Contains("org.springframework.boot", StringComparison.Ordinal))
		{
			yield break;
		}
		var maven = buildFile == "pom.xml";
		var version = (maven ? JavaVersionRegex().Match(text) : ToolchainRegex().Match(text)) is { Success: true } m ? m.Groups[1].Value : RuntimeDefaults.Java;
		var app = new AppProfile
		{
			Stack = Stack.Java,
			Framework = "Spring Boot",
			RuntimeVersion = version,
			PackageManager = maven ? "maven" : "gradle",
			BuildCommand = maven ? "./mvnw -B -DskipTests package" : "./gradlew --no-daemon bootJar -x test",
			StartCommand = "java -jar /app/app.jar",
			Port = 8080,
			TestCommand = maven ? "./mvnw -B test" : "./gradlew --no-daemon test",
			HealthCheckPath = text.Contains("spring-boot-starter-actuator", StringComparison.Ordinal) ? "/actuator/health" : null,
			Confidence = Confidence.High,
		};
		app.AddEvidence(buildFile, $"Spring Boot, Java {version}");
		app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
		var candidate = new Candidate(app) { Score = 85 };
		foreach (var (artifact, kind) in new[] { ("postgresql", ServiceKind.PostgreSql), ("mysql-connector", ServiceKind.MySql), ("mariadb-java-client", ServiceKind.MariaDb), ("spring-boot-starter-data-mongodb", ServiceKind.MongoDb), ("spring-boot-starter-data-redis", ServiceKind.Redis) })
		{
			if (text.Contains(artifact, StringComparison.Ordinal))
			{
				candidate.Needs.Require(kind, new Evidence(buildFile, $"depends on {artifact}"), Confidence.High);
			}
		}
		if ((maven && !files.Exists("mvnw")) || (!maven && !files.Exists("gradlew")))
		{
			candidate.Needs.Warnings.Add($"No {(maven ? "mvnw" : "gradlew")} wrapper in the repo; the generated image uses the build tool from its base image instead.");
			app.BuildCommand = maven ? "mvn -B -DskipTests package" : "gradle --no-daemon bootJar -x test";
		}
		yield return candidate;
	}

	[GeneratedRegex(@"<java\.version>\s*(\d+)\s*</java\.version>")]
	private static partial Regex JavaVersionRegex();

	[GeneratedRegex(@"languageVersion\s*(?:=|\.set\()\s*JavaLanguageVersion\.of\((\d+)\)")]
	private static partial Regex ToolchainRegex();
}

/// <summary>Plain static sites (an index.html with no server-side stack).</summary>
internal sealed class StaticDetector : IStackDetector
{
	public IEnumerable<Candidate> Detect(RepoFiles files)
	{
		var index = files.FirstExisting("index.html", "public/index.html", "site/index.html", "docs/index.html");
		if (index is null || files.Exists("package.json"))
		{
			yield break;
		}
		var app = new AppProfile
		{
			Stack = Stack.Static,
			Framework = "Static site",
			DocumentRoot = RepoFiles.DirectoryOf(index),
			Port = 8080,
			Confidence = Confidence.Medium,
		};
		app.AddEvidence(index, "static HTML entry point");
		app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
		yield return new Candidate(app) { Score = index.StartsWith("docs/", StringComparison.Ordinal) ? 20 : 40 };
	}
}
