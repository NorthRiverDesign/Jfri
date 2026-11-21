using Jfri.Core.Analysis;

namespace Jfri.Core.Tests;

public sealed class AnalyzerTests
{
	[Fact]
	public void Laravel_app_gets_database_cache_worker_scheduler_storage_and_release()
	{
		var profile = Fixtures.Analyze("laravel-mysql");
		var app = profile.App!;

		Assert.True(profile.IsDeployable);
		Assert.Equal(Stack.Php, app.Stack);
		Assert.Equal("Laravel", app.Framework);
		Assert.Equal("8.4", app.RuntimeVersion);
		Assert.Equal("public", app.DocumentRoot);
		Assert.Equal("/up", app.HealthCheckPath);
		Assert.Equal("php artisan migrate --force", app.ReleaseCommand);
		Assert.Equal("npm run build", app.AssetBuildCommand);
		Assert.Equal("php artisan test", app.TestCommand);
		Assert.Equal(["web", "worker", "scheduler"], app.Processes.Select(p => p.Name));
		Assert.Equal(BuildStrategy.Generated, app.BuildStrategy);

		Assert.Equal([ServiceKind.MySql, ServiceKind.Redis], profile.Services.Select(s => s.Kind));
		Assert.Equal(Confidence.High, profile.Services[0].Confidence);
		Assert.Contains(profile.Services[0].Evidence, e => e.Detail == "DB_CONNECTION=mysql");
		Assert.Contains(profile.PersistentPaths, p => p.ContainerPath == "/var/www/html/storage/app");

		var stripe = profile.Environment.Single(e => e.Key == "STRIPE_SECRET");
		Assert.True(stripe.LooksSecret);
		Assert.Null(stripe.ExampleValue);
	}

	[Fact]
	public void DotNet_solution_picks_the_web_project_and_follows_project_references()
	{
		var profile = Fixtures.Analyze("aspnet-postgres");
		var app = profile.App!;

		Assert.Equal(Stack.DotNet, app.Stack);
		Assert.Equal("src/Shop.Web/Shop.Web.csproj", app.EntryProject);
		Assert.Equal("9.0", app.RuntimeVersion);
		Assert.Equal("/healthz", app.HealthCheckPath);
		Assert.Equal("dotnet test", app.TestCommand);
		Assert.Equal("dotnet Shop.Web.dll", app.StartCommand);
		Assert.Contains(profile.Services, s => s.Kind == ServiceKind.PostgreSql && s.Evidence.Any(e => e.File == "src/Shop.Data/Shop.Data.csproj"));
		Assert.Contains(profile.Services, s => s.Kind == ServiceKind.Redis);
		Assert.Contains(profile.Environment, e => e.Key == "ConnectionStrings__Shop");
		// The app calls Database.Migrate(), so no migration warning.
		Assert.DoesNotContain(profile.Warnings, w => w.Contains("migrations", StringComparison.OrdinalIgnoreCase));
		// The demo sample is a candidate, but not the one deployed.
		Assert.Contains(profile.OtherApps, o => o.EntryProject == "samples/Shop.Demo/Shop.Demo.csproj");
		Assert.DoesNotContain(profile.OtherApps, o => o.EntryProject!.Contains("Tests", StringComparison.Ordinal));
	}

	[Fact]
	public void NextJs_with_prisma_and_pnpm()
	{
		var profile = Fixtures.Analyze("next-prisma");
		var app = profile.App!;

		Assert.Equal(Stack.Node, app.Stack);
		Assert.Equal("Next.js", app.Framework);
		Assert.Equal("pnpm", app.PackageManager);
		Assert.Equal("22", app.RuntimeVersion);
		Assert.Equal("node server.js", app.StartCommand);
		Assert.Equal("npx prisma migrate deploy", app.ReleaseCommand);
		Assert.Equal(3000, app.Port);
		Assert.Equal([ServiceKind.PostgreSql], profile.Services.Select(s => s.Kind));
	}

	[Fact]
	public void Django_with_celery_and_postgres()
	{
		var profile = Fixtures.Analyze("django-celery");
		var app = profile.App!;

		Assert.Equal("Django", app.Framework);
		Assert.Equal("3.12", app.RuntimeVersion);
		Assert.StartsWith("gunicorn mysite.wsgi:application", app.StartCommand);
		Assert.Equal("python manage.py migrate --noinput", app.ReleaseCommand);
		Assert.Contains(app.Processes, p => p.Kind == ProcessKind.Worker && p.Command!.StartsWith("celery -A mysite", StringComparison.Ordinal));
		Assert.Equal([ServiceKind.PostgreSql, ServiceKind.Redis], profile.Services.Select(s => s.Kind));
		Assert.Contains(profile.PersistentPaths, p => p.ContainerPath == "/app/media");
	}

	[Fact]
	public void Go_module_with_cmd_main()
	{
		var app = Fixtures.Analyze("go-api").App!;
		Assert.Equal(Stack.Go, app.Stack);
		Assert.Equal("1.23", app.RuntimeVersion);
		Assert.Contains("./cmd/api", app.BuildCommand);
	}

	[Fact]
	public void Static_site()
	{
		var app = Fixtures.Analyze("static-site").App!;
		Assert.Equal(Stack.Static, app.Stack);
		Assert.Equal("", app.DocumentRoot);
	}

	[Fact]
	public void An_existing_dockerfile_outranks_inference_and_compose_versions_are_read()
	{
		var profile = Fixtures.Analyze("dockerfile-only");

		Assert.True(profile.IsDeployable);
		Assert.Equal(BuildStrategy.ExistingDockerfile, profile.App!.BuildStrategy);
		Assert.Equal("Dockerfile", profile.App.DockerfilePath);
		Assert.Equal(9000, profile.App.Port);
		var db = Assert.Single(profile.Services);
		Assert.Equal(ServiceKind.PostgreSql, db.Kind);
		Assert.Equal("16", db.Version);
	}

	[Fact]
	public void Unknown_repositories_say_what_was_seen_instead_of_guessing()
	{
		var profile = Fixtures.Analyze("unknown");

		Assert.False(profile.IsDeployable);
		Assert.Null(profile.App);
		Assert.Contains(profile.Observations, o => o.Contains("README.md", StringComparison.Ordinal));
	}

	[Fact]
	public void Symlinks_are_never_followed_out_of_the_checkout()
	{
		var dir = Directory.CreateTempSubdirectory("jfri-symlink-");
		try
		{
			File.WriteAllText(Path.Combine(dir.FullName, "index.html"), "<p>hi</p>");
			var outside = Directory.CreateTempSubdirectory("jfri-outside-");
			File.WriteAllText(Path.Combine(outside.FullName, "secret.txt"), "top secret");
			Directory.CreateSymbolicLink(Path.Combine(dir.FullName, "linked-dir"), outside.FullName);
			File.CreateSymbolicLink(Path.Combine(dir.FullName, "composer.json"), Path.Combine(outside.FullName, "secret.txt"));

			var files = RepoFiles.Open(dir.FullName);

			Assert.Equal(["index.html"], files.Files);
			Assert.Null(files.Read("composer.json"));
			outside.Delete(recursive: true);
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}
}

public sealed class VersionRangeTests
{
	[Theory]
	[InlineData("^8.2", "8.4")]
	[InlineData("~8.1.0", "8.1")]
	[InlineData(">=8.1 <8.4", "8.3")]
	[InlineData("^7.4|^8.0", "8.4")]
	[InlineData("8.2.*", "8.2")]
	[InlineData("^8.5", "8.5")]
	public void Php(string constraint, string expected) =>
		Assert.Equal(expected, VersionRange.Pick(constraint, RuntimeDefaults.Php, RuntimeDefaults.PhpSupported));

	[Theory]
	[InlineData(">=18", "24")]
	[InlineData(">=20 <23", "22")]
	[InlineData("20.x", "20")]
	[InlineData("^18 || ^20", "20")]
	public void Node(string constraint, string expected) =>
		Assert.Equal(expected, VersionRange.Pick(constraint, RuntimeDefaults.Node, RuntimeDefaults.NodeSupported));

	[Theory]
	[InlineData(">=3.10,<3.13", "3.12")]
	[InlineData("~=3.11", "3.13")]
	[InlineData("^3.9", "3.13")]
	public void Python(string constraint, string expected) =>
		Assert.Equal(expected, VersionRange.Pick(constraint, RuntimeDefaults.Python, RuntimeDefaults.PythonSupported));

	[Fact]
	public void Empty_constraints_mean_use_the_default() => Assert.Null(VersionRange.Pick("", "8.4", RuntimeDefaults.PhpSupported));
}
