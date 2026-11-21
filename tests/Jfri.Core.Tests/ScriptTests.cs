using System.Text;
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
}
