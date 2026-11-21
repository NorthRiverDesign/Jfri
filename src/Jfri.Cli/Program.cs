using System.Globalization;
using System.Text.Json;
using Jfri.Core.Analysis;
using Jfri.Core.Generation;
using Jfri.Core.Planning;
using Jfri.Core.Pricing;

// jfri: the command-line face of Jfri's analyzer, planner and estimator.
// It reads a local checkout; it never runs anything from it and never touches a server.

if (args.Length < 2 || args[0] is "-h" or "--help" or "help")
{
	Console.WriteLine("""
		Usage:
		  jfri analyze  <repo-dir> [--json]
		  jfri plan     <repo-dir> [--runtime kubernetes|compose] [--domain example.com] [--out <dir>]
		  jfri estimate <repo-dir> [--runtime kubernetes|compose]

		analyze   What the app is and what it needs (stack, database, volumes, processes).
		plan      The production deployment Jfri would run, and the files it generates.
		estimate  Monthly cost of that deployment at each supported provider.
		""");
	return args.Length == 0 ? 1 : 0;
}

var command = args[0];
var dir = args[1];
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var json = args.Contains("--json");

if (!Directory.Exists(dir))
{
	Console.Error.WriteLine($"Not a directory: {dir}");
	return 1;
}

var profile = RepositoryAnalyzer.Analyze(dir);
if (command == "analyze")
{
	if (json)
	{
		Console.WriteLine(JsonSerializer.Serialize(profile, ArtifactBuilder.JsonOptions));
		return 0;
	}
	PrintProfile(profile);
	return profile.IsDeployable ? 0 : 2;
}

if (!profile.IsDeployable)
{
	PrintProfile(profile);
	return 2;
}

var options = new PlanOptions
{
	Runtime = (Option("--runtime") ?? "kubernetes").StartsWith("comp", StringComparison.OrdinalIgnoreCase) ? RuntimeKind.Compose : RuntimeKind.Kubernetes,
	Domain = Option("--domain"),
};
var plan = Planner.Plan(profile, options);

if (command == "plan")
{
	Console.WriteLine($"Plan for {plan.AppName} ({plan.Runtime})");
	foreach (var decision in plan.Decisions)
	{
		Console.WriteLine($"  • {decision}");
	}
	foreach (var warning in plan.Warnings)
	{
		Console.WriteLine($"  ! {warning}");
	}
	Console.WriteLine($"  Server needs: {plan.Resources}");
	var outDir = Option("--out");
	if (outDir is not null)
	{
		foreach (var artifact in ArtifactBuilder.Build(plan, RenderContext.Preview(plan)))
		{
			var path = Path.GetFullPath(Path.Combine(outDir, artifact.Path));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, artifact.Content);
			Console.WriteLine($"  wrote {artifact.Path}");
		}
	}
	return 0;
}

if (command == "estimate")
{
	using var http = new HttpClient();
	http.DefaultRequestHeaders.UserAgent.ParseAdd("jfri-cli");
	var pricing = await new PricingClient(http).GetAllAsync(null, CancellationToken.None);
	Console.WriteLine($"{plan.AppName} needs about {plan.Resources}");
	foreach (var quote in CostEstimator.Estimate(plan, pricing))
	{
		var total = quote.Total.ToString("0.00", CultureInfo.InvariantCulture);
		Console.WriteLine($"  {quote.Provider.Name,-24} {quote.Server.Name,-28} {total,8} {quote.Currency}/mo  ({(quote.Live ? "live" : "catalog " + quote.AsOf)})");
	}
	return 0;
}

Console.Error.WriteLine($"Unknown command {command}.");
return 1;

static void PrintProfile(RepositoryProfile profile)
{
	var app = profile.App;
	if (app is null)
	{
		foreach (var observation in profile.Observations)
		{
			Console.WriteLine(observation);
		}
		return;
	}
	Console.WriteLine($"{profile.RepositoryName}: {app.Framework} ({app.Stack} {app.RuntimeVersion}), port {app.Port}{(app.RootPath.Length > 0 ? $", in {app.RootPath}/" : "")}");
	Console.WriteLine($"  Build: {(app.BuildStrategy == BuildStrategy.ExistingDockerfile ? app.DockerfilePath : "generated Dockerfile")}");
	foreach (var process in app.Processes)
	{
		Console.WriteLine($"  Process {process.Name}: {process.Command ?? "(image default)"}");
	}
	if (app.ReleaseCommand is not null)
	{
		Console.WriteLine($"  Release: {app.ReleaseCommand}");
	}
	if (app.HealthCheckPath is not null)
	{
		Console.WriteLine($"  Health: {app.HealthCheckPath}");
	}
	foreach (var service in profile.Services)
	{
		Console.WriteLine($"  Needs {service.Kind}{(service.Version is null ? "" : " " + service.Version)} ({service.Confidence}): {string.Join("; ", service.Evidence)}");
	}
	foreach (var path in profile.PersistentPaths)
	{
		Console.WriteLine($"  Volume {path.ContainerPath}: {path.Reason}");
	}
	foreach (var warning in profile.Warnings)
	{
		Console.WriteLine($"  ! {warning}");
	}
	foreach (var other in profile.OtherApps)
	{
		Console.WriteLine($"  (also found {other.Framework} in {other.EntryProject ?? (other.RootPath.Length > 0 ? other.RootPath : "the root")})");
	}
}
