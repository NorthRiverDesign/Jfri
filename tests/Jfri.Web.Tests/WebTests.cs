using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Jfri.Web.Tests;

/// <summary>A Jfri instance with its own throwaway data directory.</summary>
public sealed class JfriFactory : WebApplicationFactory<Program>
{
	public string DataDir { get; } = Directory.CreateTempSubdirectory("jfri-web-test-").FullName;

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseSetting("Jfri:DataDir", DataDir);
		builder.UseEnvironment("Testing");
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		try
		{
			Directory.Delete(DataDir, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

	public string SetupToken => File.ReadAllText(Path.Combine(DataDir, "setup-token")).Trim();

	/// <summary>Completes first-run setup and returns a signed-in client.</summary>
	public async Task<HttpClient> SignedInAsync()
	{
		var client = Browser();
		var form = await FormAsync(client, "/setup");
		var response = await client.PostAsync("/setup", Form(form, ("Token", SetupToken), ("UserName", "admin"), ("Password", "correct horse battery staple")));
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		return client;
	}

	public static async Task<string> FormAsync(HttpClient client, string path)
	{
		var html = await client.GetStringAsync(path);
		return Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
	}

	public static FormUrlEncodedContent Form(string antiforgery, params (string Key, string Value)[] fields) =>
		new([new("__RequestVerificationToken", antiforgery), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]);
}

public sealed class WebTests
{
	[Fact]
	public async Task A_fresh_instance_sends_everyone_to_setup_and_requires_the_setup_token()
	{
		using var factory = new JfriFactory();
		var client = factory.Browser();

		var home = await client.GetAsync("/");
		Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
		Assert.Equal("/setup", home.Headers.Location!.OriginalString);

		var token = await JfriFactory.FormAsync(client, "/setup");
		var wrong = await client.PostAsync("/setup", JfriFactory.Form(token, ("Token", "not-the-token"), ("UserName", "admin"), ("Password", "correct horse battery staple")));
		Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
		Assert.Contains("doesn&#x27;t match", await wrong.Content.ReadAsStringAsync());

		var right = await client.PostAsync("/setup", JfriFactory.Form(token, ("Token", factory.SetupToken), ("UserName", "admin"), ("Password", "correct horse battery staple")));
		Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
		Assert.False(File.Exists(Path.Combine(factory.DataDir, "setup-token")));

		// Signed in now, and setup is gone for good.
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/setup")).StatusCode);
	}

	[Fact]
	public async Task Pages_and_the_api_require_sign_in()
	{
		using var factory = new JfriFactory();
		await factory.SignedInAsync();
		var anonymous = factory.Browser();

		foreach (var path in new[] { "/", "/connections", "/apps/1", "/deployments/1", "/api/deployments/1/log" })
		{
			var response = await anonymous.GetAsync(path);
			Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
			Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery);
		}
	}

	[Fact]
	public async Task Login_rejects_wrong_passwords_and_accepts_the_right_one()
	{
		using var factory = new JfriFactory();
		await factory.SignedInAsync();
		var client = factory.Browser();
		var token = await JfriFactory.FormAsync(client, "/login");

		var wrong = await client.PostAsync("/login", JfriFactory.Form(token, ("UserName", "admin"), ("Password", "wrong password!!")));
		Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
		var right = await client.PostAsync("/login", JfriFactory.Form(token, ("UserName", "admin"), ("Password", "correct horse battery staple"), ("ReturnUrl", "https://evil.example/")));
		Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
		Assert.Equal("/", right.Headers.Location!.OriginalString);
	}

	[Fact]
	public async Task Responses_carry_security_headers()
	{
		using var factory = new JfriFactory();
		var response = await factory.Browser().GetAsync("/setup");

		Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
		Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
		Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
		Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
	}

	[Fact]
	public async Task Posts_without_an_antiforgery_token_are_rejected()
	{
		using var factory = new JfriFactory();
		var client = await factory.SignedInAsync();
		var response = await client.PostAsync("/connections?handler=AddConnection", new FormUrlEncodedContent([new("Provider", "hetzner"), new("Token", "abcdefghijklmnopqrstuvwxyz")]));
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Provider_tokens_are_encrypted_at_rest_and_never_shown_again()
	{
		using var factory = new JfriFactory();
		var client = await factory.SignedInAsync();
		const string secretToken = "hcloud-token-ABCDEFGHIJKLMNOPQRSTUVWXYZ-1234";
		var form = await JfriFactory.FormAsync(client, "/connections");
		var saved = await client.PostAsync("/connections?handler=AddConnection", JfriFactory.Form(form, ("Provider", "hetzner"), ("Name", "Work"), ("Token", secretToken)));
		Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

		var page = WebUtility.HtmlDecode(await client.GetStringAsync("/connections"));
		Assert.DoesNotContain(secretToken, page);
		Assert.Contains("••••1234", page);

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
		var connection = db.Connections.Single();
		Assert.DoesNotContain(secretToken, connection.EncryptedToken);
		Assert.Equal(secretToken, scope.ServiceProvider.GetRequiredService<Vault>().Unprotect(connection.EncryptedToken));
		Assert.DoesNotContain(secretToken, File.ReadAllText(Path.Combine(factory.DataDir, "jfri.db"), Encoding.Latin1));
	}

	[Fact]
	public async Task Webhooks_need_a_valid_signature_and_only_deploy_the_tracked_branch()
	{
		using var factory = new JfriFactory();
		await factory.SignedInAsync();
		int appId;
		string secret;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
			var vault = scope.ServiceProvider.GetRequiredService<Vault>();
			secret = Vault.NewWebhookSecret();
			var app = new App { Name = "shop", RepoUrl = "https://example.com/acme/shop", ProfileJson = "{}", OptionsJson = "{}", EncryptedWebhookSecret = vault.Protect(secret) };
			db.Apps.Add(app);
			db.SaveChanges();
			appId = app.Id;
		}
		var client = factory.Browser();
		async Task<HttpResponseMessage> Send(string body, string? signature)
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, $"/hooks/apps/{appId}/deploy") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
			if (signature is not null)
			{
				request.Headers.Add("X-Hub-Signature-256", signature);
			}
			return await client.SendAsync(request);
		}

		var body = """{"ref":"refs/heads/main","after":"abc"}""";
		Assert.Equal(HttpStatusCode.Unauthorized, (await Send(body, null)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await Send(body, Webhooks.Sign("wrong-secret", Encoding.UTF8.GetBytes(body)))).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await Send(body, "sha256=zz")).StatusCode);

		var otherBranch = """{"ref":"refs/heads/feature","after":"abc"}""";
		Assert.Equal(HttpStatusCode.Accepted, (await Send(otherBranch, Webhooks.Sign(secret, Encoding.UTF8.GetBytes(otherBranch)))).StatusCode);
		// Valid, but the app has never been deployed, so there's no server to deploy to.
		Assert.Equal(HttpStatusCode.Conflict, (await Send(body, Webhooks.Sign(secret, Encoding.UTF8.GetBytes(body)))).StatusCode);
	}

	[Fact]
	public async Task Bad_repository_urls_are_refused_before_anything_is_cloned()
	{
		using var factory = new JfriFactory();
		var client = await factory.SignedInAsync();
		var form = await JfriFactory.FormAsync(client, "/");
		var response = await client.PostAsync("/?handler=Connect", JfriFactory.Form(form, ("RepoUrl", "file:///etc")));
		var html = await response.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("https://", html);
		Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(factory.DataDir, "workspaces")));
	}
}
