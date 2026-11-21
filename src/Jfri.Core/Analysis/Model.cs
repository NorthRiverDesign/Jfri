using System.Text.Json.Serialization;

namespace Jfri.Core.Analysis;

[JsonConverter(typeof(JsonStringEnumConverter<Stack>))]
public enum Stack
{
	Unknown,
	DotNet,
	Php,
	Node,
	Python,
	Ruby,
	Go,
	Java,
	Static,
}

[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
	Low,
	Medium,
	High,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceKind>))]
public enum ServiceKind
{
	PostgreSql,
	MySql,
	MariaDb,
	SqlServer,
	MongoDb,
	Redis,
	Sqlite,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProcessKind>))]
public enum ProcessKind
{
	Web,
	Worker,
	Scheduler,
}

[JsonConverter(typeof(JsonStringEnumConverter<BuildStrategy>))]
public enum BuildStrategy
{
	/// <summary>Jfri writes a Dockerfile for the detected stack.</summary>
	Generated,
	/// <summary>The repository ships its own Dockerfile; it outranks inference.</summary>
	ExistingDockerfile,
}

/// <summary>A file (and what was seen in it) that supports a finding.</summary>
public sealed record Evidence(string File, string Detail)
{
	public override string ToString() => $"{File}: {Detail}";
}

/// <summary>A long-running or scheduled process the app needs besides (or as) its web server.</summary>
public sealed record ProcessSpec(string Name, ProcessKind Kind, string? Command);

/// <summary>A backing service (database, cache) the app needs.</summary>
public sealed class ServiceRequirement
{
	public required ServiceKind Kind { get; init; }
	public string? Version { get; set; }
	public Confidence Confidence { get; set; } = Confidence.Medium;
	public List<Evidence> Evidence { get; init; } = [];
}

/// <summary>A directory inside the app container whose contents must survive restarts and redeploys.</summary>
public sealed record PersistentPath(string ContainerPath, string Reason, Evidence Evidence);

/// <summary>An environment variable the app reads, as declared by the repo (e.g. .env.example).</summary>
public sealed record EnvironmentKey(string Key, string? ExampleValue, bool LooksSecret, string Source);

/// <summary>Everything Jfri inferred about one deployable application inside the repository.</summary>
public sealed class AppProfile
{
	public Stack Stack { get; set; } = Stack.Unknown;
	/// <summary>Human-readable framework name, e.g. "Laravel", "ASP.NET Core", "Next.js".</summary>
	public string? Framework { get; set; }
	/// <summary>Runtime version the container should use, e.g. "10.0", "8.4", "24", "3.13".</summary>
	public string? RuntimeVersion { get; set; }
	/// <summary>Directory of the app within the repo, "" for the root. Forward slashes.</summary>
	public string RootPath { get; set; } = "";
	/// <summary>For .NET: the web project to publish, relative to the repo root.</summary>
	public string? EntryProject { get; set; }
	/// <summary>Package manager / build tool: npm, pnpm, yarn, bun, composer, pip, poetry, uv, maven, gradle, go, bundler, dotnet.</summary>
	public string? PackageManager { get; set; }
	public string? BuildCommand { get; set; }
	public string? StartCommand { get; set; }
	/// <summary>Command run once per release before new code takes traffic (database migrations).</summary>
	public string? ReleaseCommand { get; set; }
	/// <summary>Command that builds front-end assets during the image build (e.g. Vite for Laravel).</summary>
	public string? AssetBuildCommand { get; set; }
	/// <summary>Port the container listens on.</summary>
	public int Port { get; set; } = 8080;
	public string? HealthCheckPath { get; set; }
	/// <summary>Directory served as the web root (PHP apps).</summary>
	public string? DocumentRoot { get; set; }
	/// <summary>Command the repo uses for its tests, used by the generated CI workflow.</summary>
	public string? TestCommand { get; set; }
	public BuildStrategy BuildStrategy { get; set; } = BuildStrategy.Generated;
	/// <summary>Repo-relative path of an existing Dockerfile, when <see cref="BuildStrategy"/> is ExistingDockerfile.</summary>
	public string? DockerfilePath { get; set; }
	public List<ProcessSpec> Processes { get; init; } = [];
	public Confidence Confidence { get; set; } = Confidence.Medium;
	public List<Evidence> Evidence { get; init; } = [];

	public void AddEvidence(string file, string detail) => Evidence.Add(new Evidence(file, detail));
}

/// <summary>The result of analyzing a repository: what it is, and what it needs to run in production.</summary>
public sealed class RepositoryProfile
{
	public string? RepositoryName { get; set; }
	/// <summary>The application Jfri will deploy. Null when nothing deployable was recognised.</summary>
	public AppProfile? App { get; set; }
	/// <summary>Other candidate apps seen in the repo (monorepos). Informational.</summary>
	public List<AppProfile> OtherApps { get; init; } = [];
	public List<ServiceRequirement> Services { get; init; } = [];
	public List<PersistentPath> PersistentPaths { get; init; } = [];
	public List<EnvironmentKey> Environment { get; init; } = [];
	/// <summary>Existing deployment artifacts in the repo (Dockerfile, compose, k8s manifests, CI).</summary>
	public List<Evidence> ExistingArtifacts { get; init; } = [];
	public List<string> Warnings { get; init; } = [];
	/// <summary>When no app was recognised: what Jfri saw, so the user knows why.</summary>
	public List<string> Observations { get; init; } = [];
	public int FilesScanned { get; set; }
	public bool Truncated { get; set; }

	[JsonIgnore]
	public bool IsDeployable => App is not null && (App.Stack != Stack.Unknown || App.BuildStrategy == BuildStrategy.ExistingDockerfile);

	public ServiceRequirement? Database => Services.FirstOrDefault(s => s.Kind is ServiceKind.PostgreSql or ServiceKind.MySql or ServiceKind.MariaDb or ServiceKind.SqlServer or ServiceKind.MongoDb);

	public bool Needs(ServiceKind kind) => Services.Any(s => s.Kind == kind);

	/// <summary>Adds (or strengthens) a service requirement.</summary>
	public ServiceRequirement Require(ServiceKind kind, Evidence evidence, Confidence confidence = Confidence.Medium, string? version = null)
	{
		var existing = Services.FirstOrDefault(s => s.Kind == kind);
		if (existing is null)
		{
			existing = new ServiceRequirement { Kind = kind, Confidence = confidence, Version = version };
			Services.Add(existing);
		}
		else
		{
			if (confidence > existing.Confidence)
			{
				existing.Confidence = confidence;
			}
			existing.Version ??= version;
		}
		if (!existing.Evidence.Contains(evidence))
		{
			existing.Evidence.Add(evidence);
		}
		return existing;
	}

	public void AddPersistentPath(string containerPath, string reason, Evidence evidence)
	{
		if (PersistentPaths.All(p => p.ContainerPath != containerPath))
		{
			PersistentPaths.Add(new PersistentPath(containerPath, reason, evidence));
		}
	}

	public void AddEnvironment(EnvironmentKey key)
	{
		if (Environment.All(e => e.Key != key.Key))
		{
			Environment.Add(key);
		}
	}
}

/// <summary>Default and supported runtime versions, newest well-supported first in preference.</summary>
public static class RuntimeDefaults
{
	public const string DotNet = "10.0";
	public const string Php = "8.4";
	public static readonly string[] PhpSupported = ["7.4", "8.0", "8.1", "8.2", "8.3", "8.4", "8.5"];
	public const string Node = "24";
	public static readonly string[] NodeSupported = ["18", "20", "22", "24", "26"];
	public const string Python = "3.13";
	public static readonly string[] PythonSupported = ["3.9", "3.10", "3.11", "3.12", "3.13", "3.14"];
	public const string Ruby = "3.4";
	public const string Go = "1.25";
	public const string Java = "21";
}
