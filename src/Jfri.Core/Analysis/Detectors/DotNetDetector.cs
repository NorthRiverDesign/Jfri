using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jfri.Core.Analysis.Detectors;

/// <summary>
/// Detects ASP.NET Core apps. In a multi-project solution it picks the project built with the Web
/// SDK (skipping tests), and follows its ProjectReferences to find database and cache packages
/// declared in class libraries.
/// </summary>
internal sealed partial class DotNetDetector : IStackDetector
{
	static readonly Dictionary<string, ServiceKind> PackageServices = new(StringComparer.OrdinalIgnoreCase)
	{
		["Npgsql"] = ServiceKind.PostgreSql,
		["Npgsql.EntityFrameworkCore.PostgreSQL"] = ServiceKind.PostgreSql,
		["Aspire.Npgsql"] = ServiceKind.PostgreSql,
		["Pomelo.EntityFrameworkCore.MySql"] = ServiceKind.MySql,
		["MySqlConnector"] = ServiceKind.MySql,
		["MySql.Data"] = ServiceKind.MySql,
		["MySql.EntityFrameworkCore"] = ServiceKind.MySql,
		["Microsoft.EntityFrameworkCore.SqlServer"] = ServiceKind.SqlServer,
		["Microsoft.Data.SqlClient"] = ServiceKind.SqlServer,
		["System.Data.SqlClient"] = ServiceKind.SqlServer,
		["Microsoft.EntityFrameworkCore.Sqlite"] = ServiceKind.Sqlite,
		["Microsoft.Data.Sqlite"] = ServiceKind.Sqlite,
		["MongoDB.Driver"] = ServiceKind.MongoDb,
		["MongoDB.EntityFrameworkCore"] = ServiceKind.MongoDb,
		["StackExchange.Redis"] = ServiceKind.Redis,
		["Microsoft.Extensions.Caching.StackExchangeRedis"] = ServiceKind.Redis,
		["Microsoft.AspNetCore.DataProtection.StackExchangeRedis"] = ServiceKind.Redis,
	};

	sealed record ProjectInfo(string Path, XDocument Xml, string Sdk, HashSet<string> Packages, List<string> References, bool IsTest);

	public IEnumerable<Candidate> Detect(RepoFiles files)
	{
		var projectPaths = files.FindByName("*.csproj").Concat(files.FindByName("*.fsproj")).Concat(files.FindByName("*.vbproj")).ToList();
		if (projectPaths.Count == 0)
		{
			yield break;
		}
		var projects = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);
		foreach (var path in projectPaths)
		{
			var info = Load(files, path);
			if (info is not null)
			{
				projects[path] = info;
			}
		}
		var hasTests = projects.Values.Any(p => p.IsTest);
		var webProjects = projects.Values.Where(p => !p.IsTest && p.Sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)).ToList();
		foreach (var web in webProjects)
		{
			yield return BuildCandidate(files, web, projects, hasTests);
		}
		if (webProjects.Count == 0)
		{
			// A worker service can still be deployed, but there is nothing to serve on the web.
			var worker = projects.Values.FirstOrDefault(p => !p.IsTest && p.Sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase));
			if (worker is not null)
			{
				var c = BuildCandidate(files, worker, projects, hasTests);
				c.App.Framework = ".NET Worker Service";
				c.App.Processes.Clear();
				c.App.Processes.Add(new ProcessSpec("worker", ProcessKind.Worker, null));
				c.App.HealthCheckPath = null;
				c.App.Confidence = Confidence.Medium;
				c.Score -= 40;
				yield return c;
			}
		}
	}

	static Candidate BuildCandidate(RepoFiles files, ProjectInfo web, Dictionary<string, ProjectInfo> projects, bool hasTests)
	{
		var dir = RepoFiles.DirectoryOf(web.Path);
		var version = TargetFramework(files, web) ?? RuntimeDefaults.DotNet;
		var app = new AppProfile
		{
			Stack = Stack.DotNet,
			Framework = "ASP.NET Core",
			RuntimeVersion = version,
			RootPath = "",
			EntryProject = web.Path,
			PackageManager = "dotnet",
			BuildCommand = $"dotnet publish \"{web.Path}\" -c Release -o /app/publish",
			StartCommand = $"dotnet {AssemblyName(web)}.dll",
			Port = 8080,
			TestCommand = hasTests ? "dotnet test" : null,
			Confidence = Confidence.High,
		};
		app.AddEvidence(web.Path, $"project uses {web.Sdk}, targets net{version}");
		app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
		var candidate = new Candidate(app) { Score = 100 };
		if (files.Exists(RepoFiles.Join(dir, "Program.cs")))
		{
			candidate.Score += 10;
		}
		var name = RepoFiles.FileName(web.Path);
		if (Regex.IsMatch(name, "(Web|Api|Server|Host|App)\\.", RegexOptions.IgnoreCase))
		{
			candidate.Score += 5;
		}
		if (Regex.IsMatch(name, "(Sample|Example|Demo|Benchmark)", RegexOptions.IgnoreCase))
		{
			candidate.Score -= 30;
		}
		candidate.Score -= web.Path.Count(c => c == '/');

		// Follow ProjectReferences so packages declared in a data-access library count too.
		var closure = Closure(web, projects);
		foreach (var project in closure)
		{
			foreach (var package in project.Packages)
			{
				if (PackageServices.TryGetValue(package, out var kind))
				{
					candidate.Needs.Require(kind, new Evidence(project.Path, $"references {package}"), Confidence.High);
				}
			}
		}

		var sources = closure.SelectMany(p => files.FindByName("*.cs", RepoFiles.DirectoryOf(p.Path))).Distinct().Take(2000).ToList();
		DetectCode(files, sources, candidate);
		DetectAppSettings(files, dir, candidate);

		// Data Protection keys must survive restarts or every redeploy logs users out and breaks antiforgery tokens.
		if (!candidate.Needs.Needs(ServiceKind.Redis))
		{
			candidate.Needs.AddPersistentPath("/home/app/.aspnet/DataProtection-Keys",
				"ASP.NET Core Data Protection keys (cookies, antiforgery) must outlive the container",
				new Evidence(web.Path, "ASP.NET Core app"));
		}
		return candidate;
	}

	static void DetectCode(RepoFiles files, List<string> sources, Candidate candidate)
	{
		var hasMigrations = false;
		var migratesAtStartup = false;
		foreach (var file in sources)
		{
			if (file.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
			{
				hasMigrations = true;
				candidate.App.AddEvidence(file, "Entity Framework Core migrations");
				continue;
			}
			var text = files.Read(file);
			if (text is null)
			{
				continue;
			}
			if (candidate.App.HealthCheckPath is null && HealthRegex().Match(text) is { Success: true } health)
			{
				candidate.App.HealthCheckPath = health.Groups[1].Value;
				candidate.App.AddEvidence(file, $"maps health checks at {health.Groups[1].Value}");
			}
			if (MigrateRegex().IsMatch(text))
			{
				migratesAtStartup = true;
			}
		}
		if (hasMigrations && !migratesAtStartup)
		{
			candidate.Needs.Warnings.Add("EF Core migrations were found but the app doesn't appear to apply them. Call Database.Migrate() at startup or add a release command, or the schema won't be created.");
		}
	}

	static void DetectAppSettings(RepoFiles files, string dir, Candidate candidate)
	{
		var path = RepoFiles.Join(dir, "appsettings.json");
		using var doc = Parsing.Json(files.Read(path));
		if (doc?.RootElement.GetObject("ConnectionStrings") is not { } connectionStrings)
		{
			return;
		}
		foreach (var cs in connectionStrings.EnumerateObject())
		{
			var key = $"ConnectionStrings__{cs.Name}";
			candidate.Needs.AddEnvironment(new EnvironmentKey(key, null, true, path));
			var value = cs.Value.ValueKind == System.Text.Json.JsonValueKind.String ? cs.Value.GetString() ?? "" : "";
			var evidence = new Evidence(path, $"connection string \"{cs.Name}\"");
			if (value.Contains("Host=", StringComparison.OrdinalIgnoreCase))
			{
				candidate.Needs.Require(ServiceKind.PostgreSql, evidence);
			}
			else if (Regex.IsMatch(value, @"Data Source=[^;]*\.(db|sqlite3?)", RegexOptions.IgnoreCase))
			{
				candidate.Needs.Require(ServiceKind.Sqlite, evidence);
			}
			else if (value.Contains("mongodb://", StringComparison.OrdinalIgnoreCase))
			{
				candidate.Needs.Require(ServiceKind.MongoDb, evidence);
			}
		}
	}

	static List<ProjectInfo> Closure(ProjectInfo root, Dictionary<string, ProjectInfo> projects)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal) { root.Path };
		var result = new List<ProjectInfo> { root };
		var queue = new Queue<ProjectInfo>([root]);
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			foreach (var reference in current.References)
			{
				if (seen.Add(reference) && projects.TryGetValue(reference, out var info))
				{
					result.Add(info);
					queue.Enqueue(info);
				}
			}
		}
		return result;
	}

	static ProjectInfo? Load(RepoFiles files, string path)
	{
		var xml = Parsing.Xml(files.Read(path));
		if (xml?.Root is null)
		{
			return null;
		}
		var root = xml.Root;
		var sdk = (string?)root.Attribute("Sdk")
			?? (string?)root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")
			?? (string?)root.Elements().FirstOrDefault(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null)?.Attribute("Sdk")
			?? "";
		var packages = root.Descendants().Where(e => e.Name.LocalName == "PackageReference")
			.Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
			.OfType<string>()
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var dir = RepoFiles.DirectoryOf(path);
		var references = root.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
			.Select(e => (string?)e.Attribute("Include"))
			.OfType<string>()
			.Select(r => ResolveRelative(dir, r))
			.ToList();
		var name = RepoFiles.FileName(path);
		var isTest = packages.Contains("Microsoft.NET.Test.Sdk")
			|| root.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
			|| Regex.IsMatch(name, @"[.\-_]Tests?\.(cs|fs|vb)proj$", RegexOptions.IgnoreCase);
		return new ProjectInfo(path, xml, sdk.Trim(), packages, references, isTest);
	}

	static string? TargetFramework(RepoFiles files, ProjectInfo project)
	{
		static string? FromXml(XDocument? doc)
		{
			var tf = doc?.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")?.Value;
			if (tf is null)
			{
				return null;
			}
			return tf.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(t => TfmRegex().Match(t))
				.Where(m => m.Success)
				.Select(m => m.Groups[1].Value)
				.OrderByDescending(VersionRange.ParseVersion)
				.FirstOrDefault();
		}
		var own = FromXml(project.Xml);
		if (own is not null)
		{
			return own;
		}
		// Walk up looking for Directory.Build.props, the way MSBuild does.
		var dir = RepoFiles.DirectoryOf(project.Path);
		while (true)
		{
			var props = FromXml(Parsing.Xml(files.Read(RepoFiles.Join(dir, "Directory.Build.props"))));
			if (props is not null)
			{
				return props;
			}
			if (dir.Length == 0)
			{
				return null;
			}
			dir = RepoFiles.DirectoryOf(dir);
		}
	}

	static string AssemblyName(ProjectInfo project)
	{
		var explicitName = project.Xml.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim();
		if (!string.IsNullOrEmpty(explicitName) && !explicitName.Contains('$'))
		{
			return explicitName;
		}
		var file = RepoFiles.FileName(project.Path);
		return file[..file.LastIndexOf('.')];
	}

	static string ResolveRelative(string baseDir, string relative)
	{
		var parts = new List<string>(baseDir.Length == 0 ? [] : baseDir.Split('/'));
		foreach (var segment in relative.Replace('\\', '/').Split('/'))
		{
			if (segment is "" or ".")
			{
				continue;
			}
			if (segment == "..")
			{
				if (parts.Count > 0)
				{
					parts.RemoveAt(parts.Count - 1);
				}
				continue;
			}
			parts.Add(segment);
		}
		return string.Join('/', parts);
	}

	[GeneratedRegex(@"^net(\d+\.\d+)$", RegexOptions.IgnoreCase)]
	private static partial Regex TfmRegex();

	[GeneratedRegex(@"MapHealthChecks\(\s*""(/[A-Za-z0-9/._~-]*)""")]
	private static partial Regex HealthRegex();

	[GeneratedRegex(@"\.(Migrate|MigrateAsync)\(")]
	private static partial Regex MigrateRegex();
}
