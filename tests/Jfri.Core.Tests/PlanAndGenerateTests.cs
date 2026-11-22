using System.Text.RegularExpressions;
using Jfri.Core.Analysis;
using Jfri.Core.Generation;
using Jfri.Core.Planning;
using YamlDotNet.RepresentationModel;

namespace Jfri.Core.Tests;

public sealed class PlannerTests
{
	[Fact]
	public void Laravel_plan_wires_database_cache_and_secrets()
	{
		var plan = Fixtures.Plan("laravel-mysql");

		Assert.Equal("laravel-mysql", plan.AppName);
		Assert.Equal(["db", "redis"], plan.Services.Select(s => s.Name));
		Assert.Equal("mysql:8.4", plan.Database!.Image);
		Assert.Equal("valkey/valkey:8-alpine", plan.Cache!.Image);
		Assert.Equal(["APP_KEY", "DB_PASSWORD", "DB_ROOT_PASSWORD", "REDIS_PASSWORD"], plan.Secrets.Select(s => s.Name).Order());
		Assert.Equal(2, plan.Web!.Replicas);
		Assert.Equal(1, plan.Processes.Single(p => p.Kind == ProcessKind.Scheduler).Replicas);
		Assert.Equal("php artisan migrate --force", plan.ReleaseCommand);
		Assert.True(plan.Backups.Enabled);

		string Env(string key) => plan.Environment.Single(e => e.Key == key).Value;
		Assert.Equal("mysql", Env("DB_CONNECTION"));
		Assert.Equal("db", Env("DB_HOST"));
		Assert.Equal("{{secret:DB_PASSWORD}}", Env("DB_PASSWORD"));
		Assert.Equal("{{url}}", Env("APP_URL"));
		Assert.Equal("false", Env("APP_DEBUG"));
		Assert.Contains("STRIPE_SECRET", plan.UnsetEnvironment);

		// Two web replicas + worker + scheduler + MySQL + Valkey on k3s needs a 4 GB-class server.
		Assert.True(plan.Resources.MemoryMb > 2048);
		Assert.True(plan.Resources.VCpu >= 2);
	}

	[Fact]
	public void DotNet_plan_sets_connection_strings_by_the_names_the_app_uses()
	{
		var plan = Fixtures.Plan("aspnet-postgres", RuntimeKind.Compose);

		var cs = plan.Environment.Single(e => e.Key == "ConnectionStrings__Shop").Value;
		Assert.Equal("Host=db;Port=5432;Database=app;Username=app;Password={{secret:DB_PASSWORD}}", cs);
		Assert.Equal("redis:6379,password={{secret:REDIS_PASSWORD}}", plan.Environment.Single(e => e.Key == "ConnectionStrings__Redis").Value);
		Assert.Equal("true", plan.Environment.Single(e => e.Key == "ASPNETCORE_FORWARDEDHEADERS_ENABLED").Value);
		Assert.Equal("postgres:17-alpine", plan.Database!.Image);
		Assert.Equal(1, plan.Web!.Replicas);
	}

	[Fact]
	public void Repo_declared_database_versions_are_kept()
	{
		var plan = Fixtures.Plan("dockerfile-only");
		Assert.Equal("postgres:16-alpine", plan.Database!.Image);
		Assert.Equal("", plan.BuildContext);
	}

	[Theory]
	[InlineData("My Great App!", "my-great-app")]
	[InlineData("123", "app-123")]
	[InlineData("--", "app")]
	[InlineData("kube-system", "app-kube-system")]
	[InlineData("Default", "app-default")]
	public void Names_are_dns_safe(string input, string expected)
	{
		Assert.Equal(expected, Planner.Slug(input));
		Assert.True(Planner.IsValidSlug(Planner.Slug(input)));
	}

	[Fact]
	public void Secrets_are_generated_once_and_kept()
	{
		var plan = Fixtures.Plan("laravel-mysql");
		var first = SecretGenerator.Fill(plan.Secrets, null);
		var second = SecretGenerator.Fill(plan.Secrets, first);

		Assert.Equal(first, second);
		Assert.StartsWith("base64:", first["APP_KEY"]);
		Assert.Equal(32, Convert.FromBase64String(first["APP_KEY"]["base64:".Length..]).Length);
		Assert.Matches("^[A-Za-z0-9]{32}$", first["DB_PASSWORD"]);
		Assert.NotEqual(first["DB_PASSWORD"], first["REDIS_PASSWORD"]);
	}
}

public sealed class GeneratorTests
{
	static readonly Dictionary<string, string> Secrets = new()
	{
		["APP_KEY"] = "base64:c2VjcmV0LWFwcC1rZXktc2VjcmV0LWFwcC1rZXktMTI=",
		["DB_PASSWORD"] = "DbPassw0rdDbPassw0rdDbPassw0rd12",
		["DB_ROOT_PASSWORD"] = "R00tPasswordR00tPasswordR00tPass",
		["REDIS_PASSWORD"] = "RedisPasswordRedisPasswordRedis1",
	};

	static List<YamlDocument> ParseYaml(string text)
	{
		var stream = new YamlStream();
		stream.Load(new StringReader(text));
		return [.. stream.Documents];
	}

	[Theory]
	[InlineData("laravel-mysql")]
	[InlineData("aspnet-postgres")]
	[InlineData("next-prisma")]
	[InlineData("django-celery")]
	[InlineData("go-api")]
	[InlineData("static-site")]
	[InlineData("dockerfile-only")]
	public void Every_generated_yaml_file_parses(string fixture)
	{
		foreach (var runtime in new[] { RuntimeKind.Kubernetes, RuntimeKind.Compose })
		{
			var plan = Fixtures.Plan(fixture, runtime);
			var files = ArtifactBuilder.Build(plan, new RenderContext("shop.example.com", "abc123-20261121t101500", SecretGenerator.Fill(plan.Secrets, Secrets)));
			foreach (var file in files.Where(f => f.Path.EndsWith(".yaml", StringComparison.Ordinal) || f.Path.EndsWith(".yml", StringComparison.Ordinal)))
			{
				var docs = ParseYaml(file.Content);
				Assert.NotEmpty(docs);
			}
		}
	}

	[Fact]
	public void Kubernetes_workloads_are_hardened_and_secrets_stay_in_the_secret_file()
	{
		var plan = Fixtures.Plan("laravel-mysql");
		var files = KubernetesGenerator.Generate(plan, new RenderContext("shop.example.com", "rev1", Secrets));

		var secretsFile = files.Single(f => f.Path == "k8s/10-secrets.yaml");
		Assert.Equal("0600", secretsFile.Mode);
		Assert.True(secretsFile.Sensitive);
		Assert.Contains(Secrets["DB_PASSWORD"], secretsFile.Content);
		foreach (var file in files.Where(f => !f.Sensitive))
		{
			foreach (var value in Secrets.Values)
			{
				Assert.DoesNotContain(value, file.Content);
			}
		}

		var app = files.Single(f => f.Path == "k8s/40-app.yaml").Content;
		var deployments = ParseYaml(app).Select(d => (YamlMappingNode)d.RootNode).Where(n => ((YamlScalarNode)n["kind"]).Value == "Deployment").ToList();
		Assert.Equal(3, deployments.Count);
		foreach (var deployment in deployments)
		{
			var pod = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)deployment["spec"])["template"])["spec"];
			var podSecurity = (YamlMappingNode)pod["securityContext"];
			Assert.Equal("true", ((YamlScalarNode)podSecurity["runAsNonRoot"]).Value);
			Assert.Equal("33", ((YamlScalarNode)podSecurity["runAsUser"]).Value);
			var container = (YamlMappingNode)((YamlSequenceNode)pod["containers"])[0];
			Assert.Equal("false", ((YamlScalarNode)((YamlMappingNode)container["securityContext"])["allowPrivilegeEscalation"]).Value);
			Assert.True(((YamlMappingNode)container["resources"]).Children.ContainsKey("limits"));
			Assert.Equal("jfri/laravel-mysql:rev1", ((YamlScalarNode)container["image"]).Value);
		}
		Assert.Contains("httpGet", app);
		Assert.Contains("path: \"/up\"", app);
		Assert.Contains("kind: PodDisruptionBudget", app);

		var ingress = files.Single(f => f.Path == "k8s/50-ingress.yaml").Content;
		Assert.Contains("host: \"shop.example.com\"", ingress);
		Assert.Contains("certresolver: letsencrypt", ingress);
		Assert.Contains("kind: NetworkPolicy", files.Single(f => f.Path == "k8s/70-network-policy.yaml").Content);
		Assert.Contains("kind: CronJob", files.Single(f => f.Path == "k8s/60-backups.yaml").Content);
		Assert.Contains("release-rev1", files.Single(f => f.Path == "k8s/30-release.yaml").Content);
	}

	[Fact]
	public void Compose_keeps_databases_private_and_escapes_shell_variables()
	{
		var plan = Fixtures.Plan("laravel-mysql", RuntimeKind.Compose);
		var files = ComposeGenerator.Generate(plan, new RenderContext("shop.example.com", "rev1", Secrets));
		var compose = files.Single(f => f.Path == "compose/compose.yaml").Content;
		var root = (YamlMappingNode)ParseYaml(compose)[0].RootNode;
		var services = (YamlMappingNode)root["services"];

		Assert.False(((YamlMappingNode)services["db"]).Children.ContainsKey("ports"));
		Assert.False(((YamlMappingNode)services["redis"]).Children.ContainsKey("ports"));
		Assert.Equal("true", ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)root["networks"])["internal"])["internal"]).Value);
		Assert.True(((YamlMappingNode)services["caddy"]).Children.ContainsKey("ports"));
		Assert.Contains(services.Children.Keys, k => ((YamlScalarNode)k).Value == "backup-db");

		// Compose interpolates $VAR; only Jfri's own variables may appear unescaped.
		var unescaped = Regex.Matches(compose, @"(?<!\$)\$(?!\$)(\{?[A-Za-z_(]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
		Assert.All(unescaped, v => Assert.Contains(v, new[] { "{JFRI_REVISION", "{REDIS_PASSWORD" }));

		foreach (var file in files.Where(f => !f.Sensitive))
		{
			foreach (var value in Secrets.Values)
			{
				Assert.DoesNotContain(value, file.Content);
			}
		}
		Assert.Contains($"REDIS_PASSWORD={Secrets["REDIS_PASSWORD"]}", files.Single(f => f.Path == "compose/.env").Content);
		Assert.Contains("reverse_proxy web:8080", files.Single(f => f.Path == "compose/Caddyfile").Content);
	}

	[Fact]
	public void Preview_rendering_never_contains_secret_values()
	{
		var plan = Fixtures.Plan("laravel-mysql");
		var files = ArtifactBuilder.Build(plan, RenderContext.Preview(plan));
		var secrets = files.Single(f => f.Path == "k8s/10-secrets.yaml").Content;
		Assert.Contains("DB_PASSWORD: \"<DB_PASSWORD>\"", secrets);
	}

	[Theory]
	[InlineData("laravel-mysql", "FROM serversideup/php:8.4-fpm-nginx", "USER www-data")]
	[InlineData("aspnet-postgres", "FROM mcr.microsoft.com/dotnet/aspnet:9.0", "USER $APP_UID")]
	[InlineData("next-prisma", "FROM node:22-slim", "USER node")]
	[InlineData("django-celery", "FROM python:3.12-slim", "USER 10001")]
	[InlineData("go-api", "FROM gcr.io/distroless/static-debian12:nonroot", "USER nonroot:nonroot")]
	[InlineData("static-site", "FROM nginxinc/nginx-unprivileged:stable-alpine", "EXPOSE 8080")]
	public void Dockerfiles_are_multi_stage_and_non_root(string fixture, string runtimeImage, string userLine)
	{
		var dockerfile = DockerfileGenerator.Generate(Fixtures.Plan(fixture));
		Assert.StartsWith("# syntax=docker/dockerfile:1", dockerfile);
		Assert.Contains(runtimeImage, dockerfile);
		Assert.Contains(userLine, dockerfile);
	}

	[Fact]
	public void DotNet_dockerfile_publishes_the_entry_project_and_owns_its_volumes()
	{
		var dockerfile = DockerfileGenerator.Generate(Fixtures.Plan("aspnet-postgres"));
		Assert.Contains("dotnet publish \"src/Shop.Web/Shop.Web.csproj\"", dockerfile);
		Assert.Contains("ENTRYPOINT [\"dotnet\", \"Shop.Web.dll\"]", dockerfile);
	}

	[Fact]
	public void Ci_workflow_tests_then_deploys_through_a_signed_webhook()
	{
		var plan = Fixtures.Plan("laravel-mysql");
		var workflow = CiGenerator.Generate(plan, "main");
		var root = (YamlMappingNode)ParseYaml(workflow)[0].RootNode;
		var jobs = (YamlMappingNode)root["jobs"];

		Assert.True(jobs.Children.ContainsKey("test"));
		Assert.Equal("test", ((YamlScalarNode)((YamlMappingNode)jobs["deploy"])["needs"]).Value);
		Assert.Contains("php artisan test", workflow);
		Assert.Contains("X-Hub-Signature-256", workflow);
		Assert.Contains("secrets.JFRI_WEBHOOK_SECRET", workflow);
	}

	[Fact]
	public void Bootstrap_is_idempotent_and_locks_the_firewall_down()
	{
		var script = ServerScripts.Bootstrap(RuntimeKind.Kubernetes, "ops@example.com");
		Assert.Contains("if [ -f /etc/jfri/bootstrap-kubernetes-v1 ]; then", script);
		Assert.Contains("ufw default deny incoming", script);
		Assert.Contains("--secrets-encryption", script);
		Assert.Contains("unattended-upgrades", script);
		Assert.Contains("acme.email=ops@example.com", script);
		Assert.DoesNotContain("6443", script);
	}

	[Theory]
	[InlineData("laravel-mysql")]
	[InlineData("django-celery")]
	[InlineData("aspnet-postgres")]
	[InlineData("laravel-sqlite")]
	public void A_saved_plan_renders_the_same_files_after_loading(string fixture)
	{
		// Rollbacks render from the plan saved with the deployment, so it must survive a round trip.
		foreach (var runtime in new[] { RuntimeKind.Kubernetes, RuntimeKind.Compose })
		{
			var plan = Fixtures.Plan(fixture, runtime, "shop.example.com");
			var loaded = System.Text.Json.JsonSerializer.Deserialize<DeploymentPlan>(System.Text.Json.JsonSerializer.Serialize(plan, ArtifactBuilder.JsonOptions), ArtifactBuilder.JsonOptions)!;
			var context = new RenderContext("shop.example.com", "rev1", SecretGenerator.Fill(plan.Secrets, null));

			var expected = ArtifactBuilder.Build(plan, context);
			var actual = ArtifactBuilder.Build(loaded, context);
			Assert.Equal(expected.Select(a => (a.Path, a.Content, a.Mode)), actual.Select(a => (a.Path, a.Content, a.Mode)));
		}
	}

	[Fact]
	public void Secret_settings_become_secret_environment_and_never_reach_the_config_map()
	{
		var options = new PlanOptions { SecretSettings = ["STRIPE_SECRET", "APP_KEY", "DB_PASSWORD", "lower_case", "MAIL_PASSWORD", "STRIPE_SECRET"], ExtraEnvironment = new() { ["MAIL_PASSWORD"] = "plain" } };
		var plan = Planner.Plan(Fixtures.Analyze("laravel-mysql"), options, "shop");

		Assert.Equal("{{secret:STRIPE_SECRET}}", plan.Environment.Single(e => e.Key == "STRIPE_SECRET").Value);
		// A secret setting wins over a plain value with the same name.
		Assert.Equal("{{secret:MAIL_PASSWORD}}", plan.Environment.Single(e => e.Key == "MAIL_PASSWORD").Value);
		Assert.Equal(SecretKind.UserProvided, plan.Secrets.Single(s => s.Name == "STRIPE_SECRET").Kind);
		// Overriding a generated secret keeps it generated, so removing the override falls back to it.
		Assert.Equal(SecretKind.LaravelKey, plan.Secrets.Single(s => s.Name == "APP_KEY").Kind);
		// The database password and invalid names are ignored.
		Assert.Equal(SecretKind.Password, plan.Secrets.Single(s => s.Name == "DB_PASSWORD").Kind);
		Assert.DoesNotContain(plan.Environment, e => e.Key == "lower_case");
		Assert.DoesNotContain("STRIPE_SECRET", plan.UnsetEnvironment);

		var secrets = SecretGenerator.ForDeploy(plan.Secrets, null, new Dictionary<string, string> { ["STRIPE_SECRET"] = "sk_live_abc", ["MAIL_PASSWORD"] = "hunter22", ["APP_KEY"] = "base64:mine" }).All;
		var files = KubernetesGenerator.Generate(plan, new RenderContext("shop.example.com", "rev1", secrets));
		Assert.Contains("STRIPE_SECRET: \"sk_live_abc\"", files.Single(f => f.Path == "k8s/10-secrets.yaml").Content);
		Assert.Contains("APP_KEY: \"base64:mine\"", files.Single(f => f.Path == "k8s/10-secrets.yaml").Content);
		Assert.All(files.Where(f => !f.Sensitive), f => Assert.DoesNotContain("sk_live_abc", f.Content));
	}

	[Fact]
	public void Deploy_secrets_layer_the_users_settings_over_generated_ones()
	{
		PlannedSecret[] planned = [new("DB_PASSWORD", SecretKind.Password, "db"), new("APP_KEY", SecretKind.LaravelKey, "key"), new("STRIPE_SECRET", SecretKind.UserProvided, "set by you")];
		var stored = new Dictionary<string, string> { ["DB_PASSWORD"] = "stored-db-password", ["APP_KEY"] = "base64:generated" };

		var (generated, all) = SecretGenerator.ForDeploy(planned, stored, new Dictionary<string, string> { ["APP_KEY"] = "base64:mine", ["STRIPE_SECRET"] = "sk", ["DB_PASSWORD"] = "sneaky" });

		Assert.Equal(stored, generated);
		Assert.Equal("base64:mine", all["APP_KEY"]);
		Assert.Equal("sk", all["STRIPE_SECRET"]);
		Assert.Equal("stored-db-password", all["DB_PASSWORD"]);
		var missing = Assert.Throws<InvalidOperationException>(() => SecretGenerator.ForDeploy(planned, stored, null));
		Assert.Contains("STRIPE_SECRET", missing.Message);
	}
}
