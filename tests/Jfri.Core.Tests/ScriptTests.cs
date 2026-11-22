using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jfri.Core.Execution;
using Jfri.Core.Generation;
using Jfri.Core.Planning;
using YamlDotNet.RepresentationModel;

namespace Jfri.Core.Tests;

/// <summary>Checks generated shell scripts and manifests for behaviour, not just text.</summary>
public sealed class ScriptTests
{
	static async Task<ProcessResult> Bash(string script, params string[] args) =>
		await ProcessRunner.RunAsync("bash", args, CancellationToken.None, stdin: new MemoryStream(Encoding.UTF8.GetBytes(script)), timeout: TimeSpan.FromSeconds(30));

	public static TheoryData<string, RuntimeKind> Plans()
	{
		var data = new TheoryData<string, RuntimeKind>();
		foreach (var fixture in new[] { "laravel-mysql", "laravel-sqlite", "aspnet-postgres", "next-prisma", "django-celery", "static-site", "dockerfile-only" })
		{
			data.Add(fixture, RuntimeKind.Kubernetes);
			data.Add(fixture, RuntimeKind.Compose);
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(Plans))]
	public async Task Every_generated_script_is_valid_bash(string fixture, RuntimeKind runtime)
	{
		var plan = Fixtures.Plan(fixture, runtime);
		var context = new RenderContext("shop.example.com", "abc123-rev", SecretGenerator.Fill(plan.Secrets, null));
		var scripts = new List<string>
		{
			ServerScripts.Bootstrap(runtime, "ops@example.com", 2222),
			ServerScripts.BuildAndDeploy(plan, "abc123-rev", ".jfri/Dockerfile"),
			ServerScripts.Verify(plan, "shop.example.com"),
			ServerScripts.CheckImage(plan, "abc123-rev"),
			ServerScripts.Release(plan, "abc123-rev"),
			ServerScripts.Teardown(plan.AppName),
		};
		scripts.AddRange(ArtifactBuilder.Build(plan, context).Where(a => a.Path.EndsWith(".sh", StringComparison.Ordinal)).Select(a => a.Content));
		foreach (var script in scripts)
		{
			var result = await Bash(SshRemoteHost.WrapScript(script), "-n");
			Assert.True(result.ExitCode == 0, result.Output + "\n" + script);
		}
	}

	[Fact]
	public async Task Wrapped_scripts_survive_children_that_read_stdin()
	{
		var result = await Bash(SshRemoteHost.WrapScript("set -e\ncat >/dev/null\necho SURVIVED\n"), "-s");
		Assert.Equal(0, result.ExitCode);
		Assert.Contains("SURVIVED", result.Output);
	}

	[Fact]
	public async Task A_failing_command_fails_the_wrapped_script()
	{
		var result = await Bash(SshRemoteHost.WrapScript("set -euo pipefail\nfalse\necho NOT_REACHED\n"), "-s");
		Assert.NotEqual(0, result.ExitCode);
		Assert.DoesNotContain("NOT_REACHED", result.Output);
	}

	[Fact]
	public void Release_commands_are_not_exec_ed_so_compound_commands_run_completely()
	{
		var plan = Fixtures.Plan("laravel-sqlite");
		Assert.Equal("touch /var/www/html/database/data/database.sqlite && php artisan migrate --force", plan.ReleaseCommand);
		var context = new RenderContext("blog.example.com", "rev1", SecretGenerator.Fill(plan.Secrets, null));

		var job = KubernetesGenerator.Generate(plan, context).Single(f => f.Path == "k8s/30-release.yaml").Content;
		Assert.Contains("command: [\"sh\", \"-c\", \"touch /var/www/html/database/data/database.sqlite && php artisan migrate --force\"]", job);

		var compose = ComposeGenerator.Generate(Fixtures.Plan("laravel-sqlite", RuntimeKind.Compose), context).Single(f => f.Path == "compose/compose.yaml").Content;
		Assert.Contains("command: [\"sh\", \"-c\", \"touch /var/www/html/database/data/database.sqlite && php artisan migrate --force\"]", compose);
		Assert.Contains("command: [\"sh\", \"-c\", \"exec php artisan queue:work", compose);
	}

	[Theory]
	[InlineData("laravel-mysql")]
	[InlineData("laravel-sqlite")]
	[InlineData("aspnet-postgres")]
	[InlineData("django-celery")]
	public void Every_volume_claim_exists_before_the_manifest_that_mounts_it_is_applied(string fixture)
	{
		var plan = Fixtures.Plan(fixture);
		var files = KubernetesGenerator.Generate(plan, new RenderContext("x.example.com", "rev1", SecretGenerator.Fill(plan.Secrets, null)))
			.ToDictionary(f => Path.GetFileName(f.Path), f => f.Content);
		var order = files["deploy.sh"].Split('\n')
			.Select(line => line.IndexOf("kubectl apply", StringComparison.Ordinal) is var i and >= 0 ? line[i..] : null)
			.OfType<string>()
			.SelectMany(apply => Regex.Matches(apply, @"-f ([\w.-]+\.yaml)").Select(m => m.Groups[1].Value))
			.ToList();
		Assert.Equal(order.Distinct().Count(), order.Count);
		Assert.All(files.Keys.Where(k => k.EndsWith(".yaml", StringComparison.Ordinal)), f => Assert.Contains(f, order));

		var defined = new HashSet<string>();
		foreach (var file in order)
		{
			var stream = new YamlStream();
			stream.Load(new StringReader(files[file]));
			foreach (var doc in stream.Documents.Select(d => (YamlMappingNode)d.RootNode))
			{
				if (((YamlScalarNode)doc["kind"]).Value == "PersistentVolumeClaim")
				{
					defined.Add(((YamlScalarNode)((YamlMappingNode)doc["metadata"])["name"]).Value!);
				}
			}
			foreach (Match claim in Regex.Matches(files[file], @"claimName: ([\w-]+)"))
			{
				Assert.True(defined.Contains(claim.Groups[1].Value), $"{file} mounts {claim.Groups[1].Value} before it is created");
			}
		}
		if (plan.ReleaseCommand is not null && plan.Volumes.Count > 0)
		{
			Assert.True(order.IndexOf("25-volumes.yaml") < order.IndexOf("30-release.yaml"));
		}
	}

	[Fact]
	public void The_firewall_keeps_the_servers_ssh_port_open()
	{
		var script = ServerScripts.Bootstrap(RuntimeKind.Compose, null, 2222);
		Assert.Contains("ufw allow 2222/tcp", script);
		Assert.DoesNotContain("ufw allow 22/tcp", script);
	}

	[Theory]
	[InlineData("deploy/Docker'file")]
	[InlineData("../outside/Dockerfile")]
	[InlineData("-rf/Dockerfile")]
	[InlineData("my app/Dockerfile")]
	public async Task Repository_paths_with_shell_characters_are_refused(string dockerfilePath)
	{
		var profile = Fixtures.Analyze("dockerfile-only");
		profile.App!.DockerfilePath = dockerfilePath;
		var plan = Planner.Plan(profile, new PlanOptions(), "evil");
		var request = new DeploymentRequest { Plan = plan, Revision = "rev1", Secrets = SecretGenerator.Fill(plan.Secrets, null), Host = "x.example.com", WriteSource = (_, _) => Task.CompletedTask };
		var host = new FakeRemoteHost();

		await Assert.ThrowsAsync<DeploymentFailedException>(() => new DeploymentEngine().DeployAsync(request, host, _ => { }, CancellationToken.None));
		Assert.Empty(host.Scripts);
	}

	/// <summary>
	/// Runs a server script with fake commands first on PATH (sudo just runs its arguments). Each fake
	/// records how it was called. Returns the result and the calls, in order.
	/// </summary>
	static async Task<(ProcessResult Result, string Calls)> RunWithFakes(string script, params (string Name, string Body)[] fakes)
	{
		var dir = Directory.CreateTempSubdirectory("jfri-fakes-").FullName;
		try
		{
			var calls = Path.Combine(dir, "calls.log");
			foreach (var (name, body) in fakes.Append(("sudo", "exec \"$@\"")))
			{
				var path = Path.Combine(dir, name);
				File.WriteAllText(path, $"#!/usr/bin/env bash\necho \"{name} $*\" >> '{calls}'\n{body}\n");
				if (!OperatingSystem.IsWindows())
				{
					File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
			}
			var result = await Bash(SshRemoteHost.WrapScript($"export PATH='{dir}':\"$PATH\"\n{script}"), "-s");
			return (result, File.Exists(calls) ? File.ReadAllText(calls) : "");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public async Task A_rollback_loads_a_build_docker_still_has_into_k3s()
	{
		var plan = Fixtures.Plan("static-site");
		var (result, calls) = await RunWithFakes(ServerScripts.CheckImage(plan, "old-rev"),
			("k3s", "if [ \"$5\" = ls ]; then for i in $(seq 1 2000); do echo docker.io/library/postgres:$i; done; fi; if [ \"$5\" = import ]; then cat >/dev/null; fi"),
			("docker", "if [ \"$1\" = save ]; then echo tarball; fi; exit 0"));

		Assert.True(result.ExitCode == 0, result.Output);
		Assert.Contains("docker save jfri/static-site:old-rev", calls);
		Assert.Contains("k3s ctr -n k8s.io images import -", calls);
	}

	[Fact]
	public async Task A_rollback_finds_a_build_k3s_already_has()
	{
		var plan = Fixtures.Plan("static-site");
		var (result, calls) = await RunWithFakes(ServerScripts.CheckImage(plan, "old-rev"),
			// The match comes first and lots of output follows: grep must not stop early and trip pipefail.
			("k3s", "echo docker.io/jfri/static-site:old-rev; for i in $(seq 1 20000); do echo docker.io/library/postgres:$i; done"),
			("docker", "exit 1"));

		Assert.True(result.ExitCode == 0, result.Output);
		Assert.DoesNotContain("docker", calls.Replace("docker.io", "", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(RuntimeKind.Kubernetes)]
	[InlineData(RuntimeKind.Compose)]
	public async Task A_rollback_stops_when_the_build_is_gone(RuntimeKind runtime)
	{
		var plan = Fixtures.Plan("static-site", runtime);
		var (result, _) = await RunWithFakes(ServerScripts.CheckImage(plan, "old-rev"), ("k3s", "exit 0"), ("docker", "exit 1"));

		Assert.Equal(1, result.ExitCode);
		Assert.Contains("no longer on the server", result.Output);
	}

	[Fact]
	public async Task Teardown_leaves_namespaces_jfri_did_not_create_alone()
	{
		var (result, calls) = await RunWithFakes(ServerScripts.Teardown("shop"),
			("k3s", "exit 0"),
			("docker", "exit 0"));

		Assert.Equal(1, result.ExitCode);
		Assert.Contains("wasn't created by Jfri", result.Output);
		Assert.DoesNotContain("delete", calls);
		Assert.DoesNotContain("docker", calls);
	}

	[Fact]
	public async Task Teardown_deletes_nothing_when_it_cannot_save_the_backups()
	{
		var (result, calls) = await RunWithFakes(ServerScripts.Teardown("shop"),
			("k3s", """
				case "$*" in
				  *managed-by*) echo jfri ;;
				  *"get pvc backups"*) echo pvc-1234 ;;
				  *"get pv pvc-1234"*) echo /nonexistent/jfri-test/pvc-1234 ;;
				esac
				"""),
			("docker", "exit 0"));

		Assert.Equal(1, result.ExitCode);
		Assert.Contains("nothing was deleted", result.Output);
		Assert.DoesNotContain("delete", calls);
		Assert.DoesNotContain("docker", calls);
	}

	[Fact]
	public async Task Teardown_deletes_nothing_when_it_cannot_look_up_the_backups()
	{
		var (result, calls) = await RunWithFakes(ServerScripts.Teardown("shop"),
			("k3s", """
				case "$*" in
				  *managed-by*) echo jfri ;;
				  *"get pvc backups"*) echo 'Unable to connect to the server' >&2; exit 1 ;;
				esac
				"""),
			("docker", "exit 0"));

		Assert.NotEqual(0, result.ExitCode);
		Assert.DoesNotContain("delete", calls);
		Assert.DoesNotContain("docker", calls);
	}

	/// <summary>Values Compose would mangle if they went into an env file unquoted.</summary>
	static readonly Dictionary<string, string> AwkwardValues = new(StringComparer.Ordinal)
	{
		["WITH_DOLLARS"] = "pa$word${HOME}$$",
		["WITH_HASH"] = "abc #not a comment",
		["WITH_SPACES"] = "  padded  ",
		["WITH_QUOTES"] = "it's \"quoted\" $5 back\\slash \\n",
		["URL_LIKE"] = "postgresql://u:p@db:5432/app?sslmode=disable&x=1",
		["EMPTY"] = "",
		["TRAILING_BACKSLASH"] = "abc\\",
		["QUOTE_AND_TRAILING_BACKSLASH"] = "it's\\",
	};

	static async Task<bool> HasDockerCompose()
	{
		try
		{
			return (await ProcessRunner.RunAsync("docker", ["compose", "version"], CancellationToken.None, timeout: TimeSpan.FromSeconds(20))).ExitCode == 0;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return false;
		}
	}

	public static TheoryData<string> ComposeFixtures() => new(["laravel-mysql", "laravel-sqlite", "aspnet-postgres", "next-prisma", "django-celery", "static-site", "dockerfile-only"]);

	[Theory]
	[MemberData(nameof(ComposeFixtures))]
	public async Task Compose_projects_pass_docker_compose_config_and_keep_values_exact(string fixture)
	{
		if (!await HasDockerCompose())
		{
			return; // Not installed here; CI runners have it.
		}
		const string secretValue = "sk_live_$ecret'#1 \"x\" ${HOME}";
		var options = new PlanOptions { Runtime = RuntimeKind.Compose, ExtraEnvironment = new(AwkwardValues), SecretSettings = ["STRIPE_SECRET"] };
		var plan = Planner.Plan(Fixtures.Analyze(fixture), options, fixture);
		var (_, secrets) = SecretGenerator.ForDeploy(plan.Secrets, null, new Dictionary<string, string> { ["STRIPE_SECRET"] = secretValue });
		var dir = Directory.CreateTempSubdirectory("jfri-compose-");
		try
		{
			foreach (var artifact in ComposeGenerator.Generate(plan, new RenderContext("shop.example.com", "rev1", secrets)))
			{
				var path = Path.Combine(dir.FullName, artifact.Path);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, artifact.Content);
			}
			var project = Path.Combine(dir.FullName, "compose");
			var result = await ProcessRunner.RunAsync("docker", ["compose", "config", "--format", "json", "--output", "resolved.json"], CancellationToken.None, timeout: TimeSpan.FromSeconds(60), workingDirectory: project);

			Assert.True(result.ExitCode == 0, result.Output);
			Assert.DoesNotContain("variable is not set", result.Output);
			var services = JsonNode.Parse(File.ReadAllText(Path.Combine(project, "resolved.json")))!["services"]!.AsObject();
			// The resolved output escapes every literal $ as $$.
			var env = services["web"]!["environment"]!.AsObject().ToDictionary(e => e.Key, e => e.Value?.GetValue<string>().Replace("$$", "$", StringComparison.Ordinal) ?? "");
			foreach (var (key, value) in AwkwardValues)
			{
				Assert.Equal(value, env[key]);
			}
			Assert.Equal(secretValue, env["STRIPE_SECRET"]);
			if (plan.Database is { } db)
			{
				Assert.Contains(secrets["DB_PASSWORD"], services[db.Name]!["environment"]!.AsObject().Select(e => e.Value?.GetValue<string>()));
			}
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}

	[Theory]
	[InlineData("plain-value_1.2:3/4@5", "plain-value_1.2:3/4@5")]
	[InlineData("has space", "'has space'")]
	[InlineData("", "''")]
	[InlineData("it's $x", "\"it's $$x\"")]
	[InlineData("abc\\", "\"abc\\\\\"")]
	public void Env_file_values_are_quoted_only_when_needed(string value, string expected) =>
		Assert.Equal(expected, ComposeGenerator.EnvValue(value));
}
