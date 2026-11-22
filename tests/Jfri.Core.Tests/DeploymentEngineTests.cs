using System.Text;
using Jfri.Core.Execution;
using Jfri.Core.Generation;
using Jfri.Core.Planning;
using Jfri.Core.Sources;

namespace Jfri.Core.Tests;

public sealed class DeploymentEngineTests
{
	static DeploymentRequest Request(DeploymentPlan plan, IReadOnlyDictionary<string, string> secrets) => new()
	{
		Plan = plan,
		Revision = "abc123-20261121t101500",
		Secrets = secrets,
		Host = "203-0-113-7.sslip.io",
		WriteSource = (stream, ct) => stream.WriteAsync(Encoding.UTF8.GetBytes("fake tarball"), ct).AsTask(),
	};

	[Fact]
	public async Task Runs_the_steps_in_order_and_secrets_only_travel_in_0600_files()
	{
		var plan = Fixtures.Plan("laravel-mysql");
		var secrets = SecretGenerator.Fill(plan.Secrets, null);
		var host = new FakeRemoteHost();
		var log = new List<string>();

		var url = await new DeploymentEngine().DeployAsync(Request(plan, secrets), host, log.Add, CancellationToken.None);

		Assert.Equal("https://203-0-113-7.sslip.io", url);
		Assert.Equal(
			["==> Preparing the server", "==> Uploading the source", "==> Uploading the configuration", "==> Building and releasing", "==> Checking the app answers over HTTPS"],
			log.Where(l => l.StartsWith("==>", StringComparison.Ordinal)));
		// Bootstrap first, source before config, build last.
		var events = host.Events;
		Assert.StartsWith("run:", events[0]);
		Assert.Contains("bootstrap", host.Scripts[0]);
		Assert.Equal("upload:/opt/jfri/apps/laravel-mysql/source.tar.gz", events[1]);
		Assert.Contains(events, e => e == "upload:/opt/jfri/apps/laravel-mysql/overlay/.jfri/Dockerfile");
		Assert.Contains(events, e => e == "upload:/opt/jfri/apps/laravel-mysql/k8s/deploy.sh");
		Assert.Contains("docker build --pull -f .jfri/Dockerfile -t jfri/laravel-mysql:abc123-20261121t101500", host.Scripts[^2]);
		Assert.Contains("k3s ctr", host.Scripts[^2]);
		Assert.Contains("--resolve '203-0-113-7.sslip.io:443:127.0.0.1'", host.Scripts[^1]);

		foreach (var value in secrets.Values)
		{
			Assert.All(host.Scripts, s => Assert.DoesNotContain(value, s));
			Assert.All(log, l => Assert.DoesNotContain(value, l));
			foreach (var (path, file) in host.Files.Where(f => f.Value.Content.Contains(value, StringComparison.Ordinal)))
			{
				Assert.Equal("0600", file.Mode);
			}
		}
		Assert.Equal("0600", host.Files["/opt/jfri/apps/laravel-mysql/k8s/10-secrets.yaml"].Mode);
		Assert.Contains(secrets["DB_PASSWORD"], host.Files["/opt/jfri/apps/laravel-mysql/k8s/10-secrets.yaml"].Content);
	}

	[Fact]
	public async Task Compose_runtime_uploads_compose_files_and_uses_an_existing_dockerfile()
	{
		var plan = Fixtures.Plan("dockerfile-only", RuntimeKind.Compose);
		var secrets = SecretGenerator.Fill(plan.Secrets, null);
		var host = new FakeRemoteHost();

		await new DeploymentEngine().DeployAsync(Request(plan, secrets), host, _ => { }, CancellationToken.None);

		Assert.DoesNotContain(host.Files.Keys, k => k.EndsWith(".jfri/Dockerfile", StringComparison.Ordinal));
		Assert.Equal("0600", host.Files["/opt/jfri/apps/dockerfile-only/compose/db.env"].Mode);
		Assert.Contains("docker build --pull -f Dockerfile", host.Scripts[^2]);
		Assert.DoesNotContain("k3s", host.Scripts[^2]);
	}

	[Fact]
	public async Task A_failing_step_stops_the_deployment_and_says_which_step()
	{
		var plan = Fixtures.Plan("static-site");
		var host = new FakeRemoteHost();
		host.FailWhen["docker build"] = 1;

		var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => new DeploymentEngine().DeployAsync(Request(plan, new Dictionary<string, string>()), host, _ => { }, CancellationToken.None));

		Assert.Equal("Building and releasing", error.Step);
		Assert.DoesNotContain(host.Scripts, s => s.Contains("--resolve", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("bad host;rm -rf /", "rev")]
	[InlineData("example.com", "rev$(id)")]
	public async Task Unsafe_hosts_and_revisions_are_rejected(string hostName, string revision)
	{
		var plan = Fixtures.Plan("static-site");
		var request = new DeploymentRequest { Plan = plan, Revision = revision, Secrets = new Dictionary<string, string>(), Host = hostName, WriteSource = (_, _) => Task.CompletedTask };
		await Assert.ThrowsAsync<ArgumentException>(() => new DeploymentEngine().DeployAsync(request, new FakeRemoteHost(), _ => { }, CancellationToken.None));
	}

	[Fact]
	public void Redactor_masks_secret_values()
	{
		var redact = DeploymentEngine.Redactor(new Dictionary<string, string> { ["A"] = "supersecretvalue", ["B"] = "x" });
		Assert.Equal("password=*** ok x", redact("password=supersecretvalue ok x"));
	}
}

public sealed class SourceTests
{
	[Theory]
	[InlineData("https://github.com/acme/shop", null)]
	[InlineData("https://gitlab.example.com/group/sub/repo.git", null)]
	[InlineData("http://github.com/acme/shop", "https")]
	[InlineData("git@github.com:acme/shop.git", "https")]
	[InlineData("file:///etc/passwd", "https")]
	[InlineData("ext::sh -c touch% /tmp/pwned", "https")]
	[InlineData("-uhttps://evil", "valid")]
	[InlineData("https://user:token@github.com/acme/shop", "credentials")]
	[InlineData("", "Enter")]
	public void Only_plain_https_urls_are_accepted(string url, string? errorFragment)
	{
		var error = GitRepository.ValidateUrl(url);
		if (errorFragment is null)
		{
			Assert.Null(error);
		}
		else
		{
			Assert.NotNull(error);
			Assert.Contains(errorFragment, error, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Theory]
	[InlineData("main", true)]
	[InlineData("release/2.0", true)]
	[InlineData("--upload-pack=x", false)]
	[InlineData("a..b", false)]
	[InlineData("feature branch", false)]
	public void Branch_names_are_validated(string branch, bool valid) => Assert.Equal(valid, GitRepository.IsValidBranch(branch));

	[Fact]
	public void Names_come_from_the_url() => Assert.Equal("shop", GitRepository.NameFromUrl("https://github.com/acme/shop.git"));

	[Fact]
	public async Task Archives_exactly_the_committed_files()
	{
		var dir = Directory.CreateTempSubdirectory("jfri-git-");
		try
		{
			async Task Git(params string[] args)
			{
				var result = await ProcessRunner.RunAsync("git", ["-C", dir.FullName, "-c", "user.name=t", "-c", "user.email=t@example.com", .. args], CancellationToken.None);
				Assert.True(result.ExitCode == 0, result.Output);
			}
			await Git("init", "-q");
			File.WriteAllText(Path.Combine(dir.FullName, "index.html"), "hi");
			await Git("add", "index.html");
			await Git("commit", "-q", "-m", "init");
			File.WriteAllText(Path.Combine(dir.FullName, "uncommitted.txt"), "nope");

			using var archive = new MemoryStream();
			await GitRepository.ArchiveAsync(dir.FullName, archive, CancellationToken.None);
			archive.Position = 0;
			using var gzip = new System.IO.Compression.GZipStream(archive, System.IO.Compression.CompressionMode.Decompress);
			using var tar = new System.Formats.Tar.TarReader(gzip);
			var names = new List<string>();
			while (tar.GetNextEntry() is { } entry)
			{
				names.Add(entry.Name);
			}
			Assert.Contains("index.html", names);
			Assert.DoesNotContain("uncommitted.txt", names);
			Assert.Matches("^[0-9a-f]{40}$", await GitRepository.HeadAsync(dir.FullName, CancellationToken.None));
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}

	[Fact]
	public async Task Rollback_puts_an_earlier_build_back_without_uploading_or_building()
	{
		var plan = Fixtures.Plan("laravel-mysql");
		var secrets = SecretGenerator.Fill(plan.Secrets, null);
		var host = new FakeRemoteHost();
		var log = new List<string>();
		var request = new DeploymentRequest { Plan = plan, Revision = "old123-20261120t080000", Secrets = secrets, Host = "203-0-113-7.sslip.io" };

		var url = await new DeploymentEngine().RollbackAsync(request, host, log.Add, CancellationToken.None);

		Assert.Equal("https://203-0-113-7.sslip.io", url);
		Assert.Equal(
			["==> Checking the earlier build is still on the server", "==> Uploading the configuration", "==> Releasing", "==> Checking the app answers over HTTPS"],
			log.Where(l => l.StartsWith("==>", StringComparison.Ordinal)));
		Assert.Contains("image=jfri/laravel-mysql:old123-20261120t080000", host.Scripts[0]);
		Assert.All(host.Scripts, s => Assert.DoesNotContain("docker build", s));
		Assert.All(host.Scripts, s => Assert.DoesNotContain("bootstrap", s));
		Assert.DoesNotContain(host.Files.Keys, k => k.EndsWith("source.tar.gz", StringComparison.Ordinal) || k.Contains("/overlay/", StringComparison.Ordinal));
		// The manifests are rendered for the old revision, and the release runs it.
		Assert.Contains("image: jfri/laravel-mysql:old123-20261120t080000", host.Files["/opt/jfri/apps/laravel-mysql/k8s/40-app.yaml"].Content);
		Assert.Contains("JFRI_REVISION=old123-20261120t080000 bash /opt/jfri/apps/laravel-mysql/k8s/deploy.sh", host.Scripts[^2]);
		Assert.Equal("0600", host.Files["/opt/jfri/apps/laravel-mysql/k8s/10-secrets.yaml"].Mode);
	}

	[Fact]
	public async Task Rollback_changes_nothing_when_the_build_is_gone()
	{
		var plan = Fixtures.Plan("static-site", RuntimeKind.Compose);
		var host = new FakeRemoteHost();
		host.FailWhen["image=jfri/static-site:"] = 1;
		var request = new DeploymentRequest { Plan = plan, Revision = "gone-rev", Secrets = new Dictionary<string, string>(), Host = "shop.example.com" };

		var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => new DeploymentEngine().RollbackAsync(request, host, _ => { }, CancellationToken.None));

		Assert.Equal("Checking the earlier build is still on the server", error.Step);
		Assert.Single(host.Scripts);
		Assert.Empty(host.Files);
	}

	[Fact]
	public async Task A_deployment_needs_the_source_but_a_rollback_does_not()
	{
		var plan = Fixtures.Plan("static-site");
		var request = new DeploymentRequest { Plan = plan, Revision = "abc", Secrets = new Dictionary<string, string>(), Host = "shop.example.com" };

		await Assert.ThrowsAsync<ArgumentException>(() => new DeploymentEngine().DeployAsync(request, new FakeRemoteHost(), _ => { }, CancellationToken.None));
	}

	[Fact]
	public async Task Teardown_runs_one_script_for_the_app()
	{
		var host = new FakeRemoteHost();
		var log = new List<string>();

		await new DeploymentEngine().TeardownAsync("shop", host, log.Add, CancellationToken.None);

		Assert.Equal(["==> Removing shop from the server"], log.Where(l => l.StartsWith("==>", StringComparison.Ordinal)));
		var script = Assert.Single(host.Scripts);
		Assert.Contains("name=shop", script);
		Assert.Contains("kubectl delete namespace \"$name\"", script);
		Assert.Contains("down --volumes", script);
		Assert.Empty(host.Files);
		await Assert.ThrowsAsync<ArgumentException>(() => new DeploymentEngine().TeardownAsync("kube-system", host, _ => { }, CancellationToken.None));
	}

	[Theory]
	[InlineData(null, "203.0.113.7", "shop", "shop.203-0-113-7.sslip.io")]
	[InlineData(null, "2001:db8::1", "shop", "shop.2001-db8--1.sslip.io")]
	[InlineData("shop.example.com", "203.0.113.7", "shop", "shop.example.com")]
	[InlineData(null, "box.example.net", "shop", "box.example.net")]
	public void Apps_get_their_own_address_on_a_shared_server(string? domain, string serverHost, string app, string expected) =>
		Assert.Equal(expected, DeploymentEngine.PublicHost(domain, serverHost, app));
}
