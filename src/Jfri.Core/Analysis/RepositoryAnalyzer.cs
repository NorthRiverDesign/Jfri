using System.Text.RegularExpressions;
using Jfri.Core.Analysis.Detectors;

namespace Jfri.Core.Analysis;

/// <summary>
/// Turns a checked-out repository into a <see cref="RepositoryProfile"/>: which app to deploy, how
/// to build it, and which databases, caches, volumes and settings it needs. Analysis is static:
/// it reads files and never runs anything from the repository.
/// </summary>
public static partial class RepositoryAnalyzer
{
	static readonly IStackDetector[] Detectors =
	[
		new DotNetDetector(),
		new PhpDetector(),
		new NodeDetector(),
		new PythonDetector(),
		new RubyDetector(),
		new GoDetector(),
		new JavaDetector(),
		new StaticDetector(),
	];

	public static RepositoryProfile Analyze(string directory, string? repositoryName = null)
	{
		var files = RepoFiles.Open(directory);
		return Analyze(files, repositoryName ?? new DirectoryInfo(directory).Name);
	}

	public static RepositoryProfile Analyze(RepoFiles files, string? repositoryName = null)
	{
		var profile = new RepositoryProfile
		{
			RepositoryName = repositoryName,
			FilesScanned = files.Files.Count,
			Truncated = files.Truncated,
		};
		if (files.Truncated)
		{
			profile.Warnings.Add($"The repository has more than {RepoFiles.DefaultMaxFiles:N0} files; only part of it was analyzed.");
		}

		var candidates = Detectors.SelectMany(d => d.Detect(files)).OrderByDescending(c => c.Score).ToList();
		var primary = candidates.FirstOrDefault();
		if (primary is not null)
		{
			profile.App = primary.App;
			Merge(profile, primary.Needs);
			profile.OtherApps.AddRange(candidates.Skip(1).Select(c => c.App));
		}

		DetectExistingArtifacts(files, profile);
		NormalizeServices(profile);

		if (profile.App is null)
		{
			Observe(files, profile);
		}
		else if (profile.App.Stack == Stack.Unknown && profile.App.BuildStrategy != BuildStrategy.ExistingDockerfile)
		{
			profile.App = null;
			Observe(files, profile);
		}
		return profile;
	}

	static void Merge(RepositoryProfile target, RepositoryProfile source)
	{
		foreach (var service in source.Services)
		{
			foreach (var evidence in service.Evidence)
			{
				target.Require(service.Kind, evidence, service.Confidence, service.Version);
			}
		}
		foreach (var path in source.PersistentPaths)
		{
			target.AddPersistentPath(path.ContainerPath, path.Reason, path.Evidence);
		}
		foreach (var env in source.Environment)
		{
			target.AddEnvironment(env);
		}
		target.Warnings.AddRange(source.Warnings);
	}

	static void DetectExistingArtifacts(RepoFiles files, RepositoryProfile profile)
	{
		var appDir = profile.App?.RootPath ?? "";
		var dockerfile = files.FirstExisting(RepoFiles.Join(appDir, "Dockerfile"), "Dockerfile", RepoFiles.Join(appDir, "Containerfile"), "Containerfile");
		if (dockerfile is not null)
		{
			profile.ExistingArtifacts.Add(new Evidence(dockerfile, "Dockerfile (used as-is: an existing Dockerfile outranks inference)"));
			var text = files.Read(dockerfile) ?? "";
			var app = profile.App ??= new AppProfile { Framework = "Dockerfile", Confidence = Confidence.Medium };
			app.BuildStrategy = BuildStrategy.ExistingDockerfile;
			app.DockerfilePath = dockerfile;
			if (ExposeRegex().Match(text) is { Success: true } expose && int.TryParse(expose.Groups[1].Value, out var port))
			{
				app.Port = port;
				app.AddEvidence(dockerfile, $"EXPOSE {port}");
			}
			else if (app.Stack == Stack.Unknown)
			{
				profile.Warnings.Add("The Dockerfile has no EXPOSE line; Jfri assumes the app listens on port 8080.");
			}
			if (app.Processes.Count == 0)
			{
				app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
			}
		}

		foreach (var compose in new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" }.Where(files.Exists))
		{
			profile.ExistingArtifacts.Add(new Evidence(compose, "Compose file (read for service versions)"));
			foreach (var image in Parsing.ComposeImages(files.Read(compose)))
			{
				var (name, tag) = SplitImage(image);
				ServiceKind? kind = name switch
				{
					"postgres" or "postgis/postgis" or "pgvector/pgvector" or "bitnami/postgresql" => ServiceKind.PostgreSql,
					"mysql" or "mysql/mysql-server" or "bitnami/mysql" => ServiceKind.MySql,
					"mariadb" or "bitnami/mariadb" => ServiceKind.MariaDb,
					"mongo" or "mongodb/mongodb-community-server" => ServiceKind.MongoDb,
					"redis" or "valkey/valkey" or "bitnami/redis" => ServiceKind.Redis,
					"mcr.microsoft.com/mssql/server" => ServiceKind.SqlServer,
					_ => null,
				};
				if (kind is null)
				{
					continue;
				}
				// Only refine services the app itself needs (compose files often carry dev-only extras),
				// unless no app-level signal exists at all.
				if (profile.Needs(kind.Value) || (profile.Database is null && kind != ServiceKind.Redis))
				{
					var version = VersionRange.Major(tag);
					var service = profile.Require(kind.Value, new Evidence(compose, $"uses image {image}"), Confidence.Medium);
					if (version is not null && tag is not "latest")
					{
						service.Version = version;
					}
				}
			}
		}

		if (files.FindByName("*.yml", ".github/workflows").Concat(files.FindByName("*.yaml", ".github/workflows")).Any())
		{
			profile.ExistingArtifacts.Add(new Evidence(".github/workflows", "GitHub Actions workflows"));
		}
		if (files.Exists(".gitlab-ci.yml"))
		{
			profile.ExistingArtifacts.Add(new Evidence(".gitlab-ci.yml", "GitLab CI pipeline"));
		}
		var k8s = files.Files.FirstOrDefault(f => f.StartsWith("k8s/", StringComparison.Ordinal) || f.StartsWith("kubernetes/", StringComparison.Ordinal) || f.StartsWith("helm/", StringComparison.Ordinal) || f.EndsWith("Chart.yaml", StringComparison.Ordinal));
		if (k8s is not null)
		{
			profile.ExistingArtifacts.Add(new Evidence(k8s, "Kubernetes manifests or Helm chart"));
		}
	}

	/// <summary>Resolves conflicts between signals: one primary database, SQLite only when nothing else.</summary>
	static void NormalizeServices(RepositoryProfile profile)
	{
		var servers = profile.Services.Where(s => s.Kind is ServiceKind.PostgreSql or ServiceKind.MySql or ServiceKind.MariaDb or ServiceKind.SqlServer).ToList();
		var sqlite = profile.Services.FirstOrDefault(s => s.Kind == ServiceKind.Sqlite);
		if (servers.Count > 0 && sqlite is not null)
		{
			// A server database is the production-grade choice when the app supports both.
			profile.Services.Remove(sqlite);
			if (sqlite.Confidence == Confidence.High)
			{
				profile.Warnings.Add($"The app supports both {servers[0].Kind} and SQLite; planning {servers[0].Kind}. Make sure production config selects it.");
			}
		}
		if (servers.Count > 1)
		{
			var keep = servers.OrderByDescending(s => s.Confidence).ThenByDescending(s => s.Evidence.Count).First();
			foreach (var extra in servers.Where(s => s != keep))
			{
				profile.Services.Remove(extra);
				profile.Warnings.Add($"Signals for both {keep.Kind} and {extra.Kind} were found; planning for {keep.Kind}. Override this in the plan if it's wrong.");
			}
		}
		if (profile.Needs(ServiceKind.Sqlite) && profile.App is { } app)
		{
			var path = app.Stack switch
			{
				Stack.Php => "/var/www/html/database/data",
				Stack.Ruby => "/rails/storage",
				_ => "/app/data",
			};
			profile.AddPersistentPath(path, "SQLite database file", profile.Services.First(s => s.Kind == ServiceKind.Sqlite).Evidence[0]);
			if (app.Stack == Stack.DotNet)
			{
				profile.Warnings.Add("SQLite: point the connection string at /app/data (e.g. Data Source=/app/data/app.db) so the database lives on the persistent volume.");
			}
		}
		// Keep services in a stable order: database first, then cache.
		profile.Services.Sort((a, b) => a.Kind.CompareTo(b.Kind));
	}

	static void Observe(RepoFiles files, RepositoryProfile profile)
	{
		profile.Observations.Add("No deployable app was recognised. Jfri can't infer a production setup from this repository yet.");
		var top = files.FilesIn("").Take(25).ToList();
		profile.Observations.Add(top.Count == 0 ? "The repository is empty." : $"Top-level files: {string.Join(", ", top)}");
		var extensions = files.Files.Select(Path.GetExtension).Where(e => !string.IsNullOrEmpty(e)).GroupBy(e => e!.ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} ×{g.Count()}");
		profile.Observations.Add($"Most common file types: {string.Join(", ", extensions)}");
		profile.Observations.Add("Adding a Dockerfile that EXPOSEs the app's port is the fastest way to make any stack deployable with Jfri.");
	}

	static (string Name, string Tag) SplitImage(string image)
	{
		var at = image.IndexOf('@');
		if (at >= 0)
		{
			image = image[..at];
		}
		var slash = image.LastIndexOf('/');
		var colon = image.LastIndexOf(':');
		var name = colon > slash ? image[..colon] : image;
		var tag = colon > slash ? image[(colon + 1)..] : "latest";
		if (name.StartsWith("docker.io/", StringComparison.Ordinal))
		{
			name = name[10..];
		}
		if (name.StartsWith("library/", StringComparison.Ordinal))
		{
			name = name[8..];
		}
		return (name, tag);
	}

	[GeneratedRegex(@"^\s*EXPOSE\s+(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
	private static partial Regex ExposeRegex();
}
