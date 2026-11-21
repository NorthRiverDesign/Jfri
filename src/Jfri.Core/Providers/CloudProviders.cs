using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jfri.Core.Pricing;

namespace Jfri.Core.Providers;

public sealed record CreateServerRequest(string Name, string Region, string Size, string PublicKey, bool Backups);

/// <summary>A server as reported by the provider. <see cref="Ipv4"/> is null until assigned.</summary>
public sealed record ProvisionedServer(string Id, string? Ipv4, bool Running);

/// <summary>Creates and inspects servers through a hosting provider's API. All servers run Ubuntu 24.04 LTS.</summary>
public interface IServerProvider
{
	ProviderInfo Info { get; }
	Task<ProvisionedServer> CreateAsync(string token, CreateServerRequest request, CancellationToken ct);
	Task<ProvisionedServer> GetAsync(string token, string id, CancellationToken ct);
	Task DeleteAsync(string token, string id, CancellationToken ct);
}

public sealed class ProviderApiException(string message) : Exception(message);

public abstract class JsonProviderBase(HttpClient http) : IServerProvider
{
	public abstract ProviderInfo Info { get; }
	protected abstract string BaseUrl { get; }
	public abstract Task<ProvisionedServer> CreateAsync(string token, CreateServerRequest request, CancellationToken ct);
	public abstract Task<ProvisionedServer> GetAsync(string token, string id, CancellationToken ct);
	public abstract Task DeleteAsync(string token, string id, CancellationToken ct);

	protected async Task<JsonNode?> SendAsync(HttpMethod method, string path, string token, object? body, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, BaseUrl + path);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}
		using var response = await http.SendAsync(request, ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new ProviderApiException($"{Info.Name} API {method} {path} failed ({(int)response.StatusCode}): {Describe(text)}");
		}
		return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
	}

	static string Describe(string body)
	{
		try
		{
			var node = JsonNode.Parse(body);
			var message = node?["error"]?["message"]?.GetValue<string>()
				?? node?["message"]?.GetValue<string>()
				?? node?["error"]?.ToString()
				?? node?["errors"]?[0]?["reason"]?.GetValue<string>();
			if (message is not null)
			{
				return message;
			}
		}
		catch (JsonException)
		{
		}
		return body.Length > 300 ? body[..300] : body;
	}
}

/// <summary>Hetzner Cloud (api.hetzner.cloud/v1).</summary>
public sealed class HetznerProvider(HttpClient http) : JsonProviderBase(http)
{
	public override ProviderInfo Info => ProviderCatalog.Hetzner;
	protected override string BaseUrl => "https://api.hetzner.cloud/v1";

	public override async Task<ProvisionedServer> CreateAsync(string token, CreateServerRequest request, CancellationToken ct)
	{
		var key = await SendAsync(HttpMethod.Post, "/ssh_keys", token, new { name = request.Name, public_key = request.PublicKey, labels = new { managed_by = "jfri" } }, ct);
		var keyId = key!["ssh_key"]!["id"]!.ToString();
		var created = await SendAsync(HttpMethod.Post, "/servers", token, new
		{
			name = request.Name,
			server_type = request.Size,
			image = "ubuntu-24.04",
			location = request.Region,
			ssh_keys = new[] { keyId },
			labels = new { managed_by = "jfri" },
			start_after_create = true,
		}, ct);
		var server = Parse(created!["server"]!);
		if (request.Backups)
		{
			await SendAsync(HttpMethod.Post, $"/servers/{server.Id}/actions/enable_backup", token, new { }, ct);
		}
		return server;
	}

	public override async Task<ProvisionedServer> GetAsync(string token, string id, CancellationToken ct) =>
		Parse((await SendAsync(HttpMethod.Get, $"/servers/{Uri.EscapeDataString(id)}", token, null, ct))!["server"]!);

	public override Task DeleteAsync(string token, string id, CancellationToken ct) =>
		SendAsync(HttpMethod.Delete, $"/servers/{Uri.EscapeDataString(id)}", token, null, ct);

	static ProvisionedServer Parse(JsonNode server) => new(
		server["id"]!.ToString(),
		server["public_net"]?["ipv4"]?["ip"]?.GetValue<string>(),
		server["status"]?.GetValue<string>() == "running");
}

/// <summary>DigitalOcean (api.digitalocean.com/v2).</summary>
public sealed class DigitalOceanProvider(HttpClient http) : JsonProviderBase(http)
{
	public override ProviderInfo Info => ProviderCatalog.DigitalOcean;
	protected override string BaseUrl => "https://api.digitalocean.com/v2";

	public override async Task<ProvisionedServer> CreateAsync(string token, CreateServerRequest request, CancellationToken ct)
	{
		var key = await SendAsync(HttpMethod.Post, "/account/keys", token, new { name = request.Name, public_key = request.PublicKey }, ct);
		var keyId = key!["ssh_key"]!["id"]!.GetValue<long>();
		var created = await SendAsync(HttpMethod.Post, "/droplets", token, new
		{
			name = request.Name,
			region = request.Region,
			size = request.Size,
			image = "ubuntu-24-04-x64",
			ssh_keys = new[] { keyId },
			backups = request.Backups,
			ipv6 = true,
			monitoring = true,
			tags = new[] { "jfri" },
		}, ct);
		return Parse(created!["droplet"]!);
	}

	public override async Task<ProvisionedServer> GetAsync(string token, string id, CancellationToken ct) =>
		Parse((await SendAsync(HttpMethod.Get, $"/droplets/{Uri.EscapeDataString(id)}", token, null, ct))!["droplet"]!);

	public override Task DeleteAsync(string token, string id, CancellationToken ct) =>
		SendAsync(HttpMethod.Delete, $"/droplets/{Uri.EscapeDataString(id)}", token, null, ct);

	static ProvisionedServer Parse(JsonNode droplet)
	{
		var ip = droplet["networks"]?["v4"]?.AsArray()
			.FirstOrDefault(n => n?["type"]?.GetValue<string>() == "public")?["ip_address"]?.GetValue<string>();
		return new(droplet["id"]!.ToString(), ip, droplet["status"]?.GetValue<string>() == "active");
	}
}

/// <summary>Vultr (api.vultr.com/v2).</summary>
public sealed class VultrProvider(HttpClient http) : JsonProviderBase(http)
{
	/// <summary>Ubuntu 24.04 LTS x64 (GET /v2/os).</summary>
	public const int Ubuntu2404 = 2284;

	public override ProviderInfo Info => ProviderCatalog.Vultr;
	protected override string BaseUrl => "https://api.vultr.com/v2";

	public override async Task<ProvisionedServer> CreateAsync(string token, CreateServerRequest request, CancellationToken ct)
	{
		var key = await SendAsync(HttpMethod.Post, "/ssh-keys", token, new { name = request.Name, ssh_key = request.PublicKey }, ct);
		var keyId = key!["ssh_key"]!["id"]!.GetValue<string>();
		var created = await SendAsync(HttpMethod.Post, "/instances", token, new
		{
			region = request.Region,
			plan = request.Size,
			os_id = Ubuntu2404,
			label = request.Name,
			hostname = request.Name,
			sshkey_id = new[] { keyId },
			backups = request.Backups ? "enabled" : "disabled",
			enable_ipv6 = true,
			tags = new[] { "jfri" },
		}, ct);
		return Parse(created!["instance"]!);
	}

	public override async Task<ProvisionedServer> GetAsync(string token, string id, CancellationToken ct) =>
		Parse((await SendAsync(HttpMethod.Get, $"/instances/{Uri.EscapeDataString(id)}", token, null, ct))!["instance"]!);

	public override Task DeleteAsync(string token, string id, CancellationToken ct) =>
		SendAsync(HttpMethod.Delete, $"/instances/{Uri.EscapeDataString(id)}", token, null, ct);

	static ProvisionedServer Parse(JsonNode instance)
	{
		var ip = instance["main_ip"]?.GetValue<string>();
		return new(instance["id"]!.GetValue<string>(), ip is null or "0.0.0.0" ? null : ip,
			instance["status"]?.GetValue<string>() == "active" && instance["power_status"]?.GetValue<string>() == "running");
	}
}

/// <summary>Akamai Cloud / Linode (api.linode.com/v4).</summary>
public sealed class LinodeProvider(HttpClient http) : JsonProviderBase(http)
{
	public override ProviderInfo Info => ProviderCatalog.Linode;
	protected override string BaseUrl => "https://api.linode.com/v4";

	public override async Task<ProvisionedServer> CreateAsync(string token, CreateServerRequest request, CancellationToken ct)
	{
		// Linode requires a root password even with keys; it's random, unused, and never stored.
		var rootPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)) + "!aA1";
		var created = await SendAsync(HttpMethod.Post, "/linode/instances", token, new
		{
			region = request.Region,
			type = request.Size,
			image = "linode/ubuntu24.04",
			label = request.Name,
			root_pass = rootPassword,
			authorized_keys = new[] { request.PublicKey },
			backups_enabled = request.Backups,
			tags = new[] { "jfri" },
		}, ct);
		return Parse(created!);
	}

	public override async Task<ProvisionedServer> GetAsync(string token, string id, CancellationToken ct) =>
		Parse((await SendAsync(HttpMethod.Get, $"/linode/instances/{Uri.EscapeDataString(id)}", token, null, ct))!);

	public override Task DeleteAsync(string token, string id, CancellationToken ct) =>
		SendAsync(HttpMethod.Delete, $"/linode/instances/{Uri.EscapeDataString(id)}", token, null, ct);

	static ProvisionedServer Parse(JsonNode linode) => new(
		linode["id"]!.ToString(),
		linode["ipv4"]?.AsArray().Select(n => n?.GetValue<string>()).FirstOrDefault(ip => ip is not null && !ip.StartsWith("192.168.", StringComparison.Ordinal)),
		linode["status"]?.GetValue<string>() == "running");
}

public static class ServerProviders
{
	public static IReadOnlyList<IServerProvider> Create(HttpClient http) =>
		[new HetznerProvider(http), new DigitalOceanProvider(http), new VultrProvider(http), new LinodeProvider(http)];

	/// <summary>Server names: "jfri-&lt;app&gt;-&lt;random&gt;", valid as a hostname on every provider.</summary>
	public static string ServerName(string appName)
	{
		var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
		var app = appName.Length > 40 ? appName[..40].TrimEnd('-') : appName;
		return $"jfri-{app}-{suffix}";
	}
}
