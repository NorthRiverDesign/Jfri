using System.Net;
using Jfri.Core.Analysis;
using Jfri.Core.Planning;
using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jfri.Web.Tests;

/// <summary>Running apps after the first deploy: rollbacks, taking apps down, sharing servers.</summary>
public sealed class OperationsTests
{
	static Server NewServer(string name, string host, string? runtime) =>
		new() { Name = name, Provider = "ssh", Host = host, Runtime = runtime, EncryptedPrivateKey = "not-a-real-key", PublicKey = "ssh-ed25519 AAAA test" };

	/// <summary>Pass the vault when the test opens the app's page (it shows the webhook secret).</summary>
	static App NewApp(string name, int? serverId, string? domain = null, Vault? vault = null) => new()
	{
		Name = name,
		RepoUrl = $"https://example.com/acme/{name}",
		ProfileJson = "{}",
		OptionsJson = domain is null ? "{}" : $$"""{"domain":"{{domain}}"}""",
		EncryptedWebhookSecret = vault?.Protect(Vault.NewWebhookSecret()) ?? "unused",
		ServerId = serverId,
	};

	static DeploymentPlan Plan(string appName, RuntimeKind runtime, string? domain = null) => new()
	{
		AppName = appName,
		Runtime = runtime,
		Domain = domain,
		App = new AppProfile { Stack = Stack.Static, Framework = "Static site" },
		Backups = new BackupPlan(),
		Resources = new ResourceEstimate(1, 1024, 10, 512),
	};

	[Fact]
	public async Task Apps_that_would_clash_on_a_server_are_kept_apart()
	{
		using var factory = new JfriFactory();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
		var compose = NewServer("small", "203.0.113.7", "Compose");
		var k8s = NewServer("cluster", "203.0.113.8", "Kubernetes");
		var named = NewServer("named", "box.example.net", "Kubernetes");
		db.Servers.AddRange(compose, k8s, named);
		await db.SaveChangesAsync();
		db.Apps.AddRange(NewApp("blog", compose.Id), NewApp("shop", k8s.Id), NewApp("wiki", named.Id));
		await db.SaveChangesAsync();
		var newcomer = NewApp("crm", null);
		db.Apps.Add(newcomer);
		await db.SaveChangesAsync();
		var ct = CancellationToken.None;

		// Compose: Caddy owns 80/443, so one app per server.
		Assert.Contains("A Compose server runs one app", await ServerPlacement.ProblemAsync(db, newcomer, Plan("crm", RuntimeKind.Compose), compose, ct));
		Assert.Contains("already runs Compose", await ServerPlacement.ProblemAsync(db, newcomer, Plan("crm", RuntimeKind.Kubernetes), compose, ct));
		// Kubernetes on an IP: each app gets its own <app>.<ip>.sslip.io address.
		Assert.Null(await ServerPlacement.ProblemAsync(db, newcomer, Plan("crm", RuntimeKind.Kubernetes), k8s, ct));
		// A server added by hostname has one address: the second app needs a domain.
		Assert.Contains("Give this app its own domain", await ServerPlacement.ProblemAsync(db, newcomer, Plan("crm", RuntimeKind.Kubernetes), named, ct));
		Assert.Null(await ServerPlacement.ProblemAsync(db, newcomer, Plan("crm", RuntimeKind.Kubernetes, "crm.example.com"), named, ct));
		// An app never clashes with itself on redeploy.
		var blog = await db.Apps.SingleAsync(a => a.Name == "blog");
		Assert.Null(await ServerPlacement.ProblemAsync(db, blog, Plan("blog", RuntimeKind.Compose), compose, ct));
	}

	[Fact]
	public async Task Removing_an_app_needs_its_name_and_waits_for_running_deployments()
	{
		using var factory = new JfriFactory();
		var client = await factory.SignedInAsync();
		int appId;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
			var app = NewApp("shop", null, vault: scope.ServiceProvider.GetRequiredService<Vault>());
			db.Apps.Add(app);
			await db.SaveChangesAsync();
			appId = app.Id;
		}
		bool Exists()
		{
			using var scope = factory.Services.CreateScope();
			return scope.ServiceProvider.GetRequiredService<JfriDb>().Apps.Any(a => a.Id == appId);
		}

		var form = await JfriFactory.FormAsync(client, $"/apps/{appId}");
		var wrong = await client.PostAsync($"/apps/{appId}?handler=Delete", JfriFactory.Form(form, ("ConfirmName", "shp")));
		Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
		Assert.Contains("Type shop to confirm", await wrong.Content.ReadAsStringAsync());
		Assert.True(Exists());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
			db.Deployments.Add(new Deployment { AppId = appId, Status = DeploymentStatus.Running });
			await db.SaveChangesAsync();
		}
		var busy = await client.PostAsync($"/apps/{appId}?handler=Delete", JfriFactory.Form(form, ("ConfirmName", "shop")));
		Assert.Equal(HttpStatusCode.OK, busy.StatusCode);
		Assert.Contains("queued or running", await busy.Content.ReadAsStringAsync());
		Assert.True(Exists());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
			await db.Deployments.Where(d => d.AppId == appId).ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DeploymentStatus.Failed));
		}
		var right = await client.PostAsync($"/apps/{appId}?handler=Delete", JfriFactory.Form(form, ("ConfirmName", "shop")));
		Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
		Assert.False(Exists());
	}

	[Fact]
	public async Task Taking_an_app_down_needs_its_name_and_queues_a_teardown()
	{
		using var factory = new JfriFactory();
		var client = await factory.SignedInAsync();
		int appId;
		int serverId;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
			var server = NewServer("box", "203.0.113.7", "Kubernetes");
			db.Servers.Add(server);
			await db.SaveChangesAsync();
			var app = NewApp("shop", server.Id, vault: scope.ServiceProvider.GetRequiredService<Vault>());
			db.Apps.Add(app);
			await db.SaveChangesAsync();
			(appId, serverId) = (app.Id, server.Id);
		}

		var page = await client.GetStringAsync($"/apps/{appId}");
		Assert.Contains("Take down from box", page);
		var form = await JfriFactory.FormAsync(client, $"/apps/{appId}");
		var wrong = await client.PostAsync($"/apps/{appId}?handler=TakeDown", JfriFactory.Form(form, ("ConfirmName", "")));
		Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);

		var right = await client.PostAsync($"/apps/{appId}?handler=TakeDown", JfriFactory.Form(form, ("ConfirmName", "shop")));
		Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
		Assert.StartsWith("/deployments/", right.Headers.Location!.OriginalString);
		using var check = factory.Services.CreateScope();
		var teardown = await check.ServiceProvider.GetRequiredService<JfriDb>().Deployments.SingleAsync(d => d.AppId == appId);
		Assert.Equal(DeploymentKind.Teardown, teardown.Kind);
		Assert.Equal(serverId, teardown.ServerId);
	}

	[Fact]
	public async Task Only_earlier_builds_on_the_current_server_can_be_rolled_back_to()
	{
		using var factory = new JfriFactory();
		var client = await factory.SignedInAsync();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
		var scheduler = scope.ServiceProvider.GetRequiredService<DeploymentScheduler>();
		var server = NewServer("box", "203.0.113.7", "Kubernetes");
		var oldServer = NewServer("old", "203.0.113.9", "Kubernetes");
		db.Servers.AddRange(server, oldServer);
		await db.SaveChangesAsync();
		var app = NewApp("shop", server.Id);
		db.Apps.Add(app);
		await db.SaveChangesAsync();
		Deployment Done(string revision, int serverId, string status = DeploymentStatus.Succeeded) =>
			new() { AppId = app.Id, ServerId = serverId, Status = status, Revision = revision, Commit = "abc", PlanJson = "{}" };
		var elsewhere = Done("r0", oldServer.Id);
		var earlier = Done("r1", server.Id);
		var failed = Done("r2", server.Id, DeploymentStatus.Failed);
		var live = Done("r3", server.Id);
		db.Deployments.AddRange(elsewhere, earlier, failed, live);
		await db.SaveChangesAsync();
		var ct = CancellationToken.None;

		Assert.True(await scheduler.CanRollBackToAsync(app, earlier, ct));
		Assert.False(await scheduler.CanRollBackToAsync(app, live, ct));
		Assert.False(await scheduler.CanRollBackToAsync(app, failed, ct));
		Assert.False(await scheduler.CanRollBackToAsync(app, elsewhere, ct));
		Assert.Contains("Roll back to this build", await client.GetStringAsync($"/deployments/{earlier.Id}"));
		Assert.DoesNotContain("Roll back to this build", await client.GetStringAsync($"/deployments/{live.Id}"));

		var form = await JfriFactory.FormAsync(client, $"/deployments/{earlier.Id}");
		var response = await client.PostAsync($"/deployments/{earlier.Id}?handler=Rollback", JfriFactory.Form(form));
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var rollback = await db.Deployments.AsNoTracking().SingleAsync(d => d.Kind == DeploymentKind.Rollback);
		Assert.Equal($"/deployments/{rollback.Id}", response.Headers.Location!.OriginalString);
		Assert.Equal(("r1", server.Id, "{}"), (rollback.Revision, rollback.ServerId, rollback.PlanJson));

		// Taking the app down deletes its images, so nothing before it can come back.
		db.Deployments.Add(new Deployment { AppId = app.Id, ServerId = server.Id, Kind = DeploymentKind.Teardown, Status = DeploymentStatus.Succeeded });
		await db.SaveChangesAsync();
		Assert.False(await scheduler.CanRollBackToAsync(app, earlier, ct));
	}
}
