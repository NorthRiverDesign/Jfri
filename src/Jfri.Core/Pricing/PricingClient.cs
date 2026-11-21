using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jfri.Core.Pricing;

/// <summary>
/// Fetches live price lists from provider APIs, caching them for a few hours. Any failure falls
/// back to <see cref="ProviderCatalog"/>, so an estimate is always available, and the quote says
/// which kind it is.
/// </summary>
public sealed class PricingClient(HttpClient http)
{
	static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);
	readonly ConcurrentDictionary<string, (DateTimeOffset At, ProviderPricing Pricing)> _cache = new();

	/// <summary>Price lists for every known provider. <paramref name="tokens"/> maps provider id → API token (optional).</summary>
	public async Task<IReadOnlyList<ProviderPricing>> GetAllAsync(IReadOnlyDictionary<string, string>? tokens, CancellationToken ct)
	{
		var tasks = ProviderCatalog.All.Select(p => GetAsync(p, tokens?.GetValueOrDefault(p.Id), ct));
		return await Task.WhenAll(tasks);
	}

	public async Task<ProviderPricing> GetAsync(ProviderInfo provider, string? token, CancellationToken ct)
	{
		var key = $"{provider.Id}:{(token is null ? "anon" : "token")}";
		if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor)
		{
			return cached.Pricing;
		}
		ProviderPricing pricing;
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			pricing = provider.Id switch
			{
				"linode" => await Linode(provider, timeout.Token),
				"vultr" => await Vultr(provider, timeout.Token),
				"hetzner" when token is not null => await Hetzner(provider, token, timeout.Token),
				"digitalocean" when token is not null => await DigitalOcean(provider, token, timeout.Token),
				_ => ProviderCatalog.Fallback(provider),
			};
			if (pricing.ServerTypes.Count == 0)
			{
				pricing = ProviderCatalog.Fallback(provider);
			}
		}
		catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException or FormatException)
		{
			if (ct.IsCancellationRequested)
			{
				throw;
			}
			var fallback = ProviderCatalog.Fallback(provider);
			pricing = fallback with { Notes = [.. fallback.Notes, $"Live prices unavailable ({ex.GetType().Name}); showing Jfri's {ProviderCatalog.CatalogDate} catalog."] };
		}
		_cache[key] = (DateTimeOffset.UtcNow, pricing);
		return pricing;
	}

	async Task<JsonDocument> GetJson(string url, string? bearer, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		if (bearer is not null)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
		}
		using var response = await http.SendAsync(request, ct);
		response.EnsureSuccessStatusCode();
		await using var stream = await response.Content.ReadAsStreamAsync(ct);
		return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
	}

	static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	async Task<ProviderPricing> Linode(ProviderInfo provider, CancellationToken ct)
	{
		using var doc = await GetJson("https://api.linode.com/v4/linode/types?page_size=200", null, ct);
		var types = new List<ServerType>();
		foreach (var t in doc.RootElement.GetProperty("data").EnumerateArray())
		{
			var cls = t.GetProperty("class").GetString();
			if (cls is not ("nanode" or "standard"))
			{
				continue;
			}
			decimal? backup = t.TryGetProperty("addons", out var addons) && addons.TryGetProperty("backups", out var b) && b.GetProperty("price").TryGetProperty("monthly", out var bm) && bm.ValueKind == JsonValueKind.Number ? bm.GetDecimal() : null;
			types.Add(new ServerType(
				t.GetProperty("id").GetString()!,
				t.GetProperty("label").GetString()!,
				t.GetProperty("vcpus").GetInt32(),
				t.GetProperty("memory").GetInt32(),
				t.GetProperty("disk").GetInt32() / 1024,
				t.GetProperty("price").GetProperty("monthly").GetDecimal(),
				backup));
		}
		return new ProviderPricing(provider, types, "USD", 0.25m, Live: true, Today, "api.linode.com/v4/linode/types", []);
	}

	async Task<ProviderPricing> Vultr(ProviderInfo provider, CancellationToken ct)
	{
		using var doc = await GetJson("https://api.vultr.com/v2/plans?type=vc2&per_page=500", null, ct);
		var types = new List<ServerType>();
		foreach (var p in doc.RootElement.GetProperty("plans").EnumerateArray())
		{
			var id = p.GetProperty("id").GetString()!;
			var price = p.GetProperty("monthly_cost").GetDecimal();
			var regions = p.GetProperty("locations").EnumerateArray().Select(l => l.GetString()!).ToList();
			// Skip free-tier, IPv6-only and region-limited promo plans.
			if (price <= 0 || id.Contains("free", StringComparison.Ordinal) || id.EndsWith("-v6", StringComparison.Ordinal) || regions.Count < 5)
			{
				continue;
			}
			var ram = p.GetProperty("ram").GetInt32();
			types.Add(new ServerType(id, $"Cloud Compute {(ram >= 1024 ? $"{ram / 1024} GB" : $"{ram} MB")}", p.GetProperty("vcpu_count").GetInt32(), ram, p.GetProperty("disk").GetInt32(), price, Regions: regions));
		}
		return new ProviderPricing(provider, types, "USD", 0.20m, Live: true, Today, "api.vultr.com/v2/plans", []);
	}

	async Task<ProviderPricing> Hetzner(ProviderInfo provider, string token, CancellationToken ct)
	{
		using var doc = await GetJson("https://api.hetzner.cloud/v1/server_types?per_page=50", token, ct);
		var types = new List<ServerType>();
		foreach (var t in doc.RootElement.GetProperty("server_types").EnumerateArray())
		{
			if (t.TryGetProperty("deprecation", out var dep) && dep.ValueKind == JsonValueKind.Object)
			{
				continue;
			}
			if (t.TryGetProperty("cpu_type", out var cpu) && cpu.GetString() != "shared")
			{
				continue;
			}
			var prices = t.GetProperty("prices").EnumerateArray().ToList();
			if (prices.Count == 0)
			{
				continue;
			}
			var price = prices.FirstOrDefault(p => p.GetProperty("location").GetString() == provider.DefaultRegion);
			if (price.ValueKind != JsonValueKind.Object)
			{
				price = prices[0];
			}
			var monthly = decimal.Parse(price.GetProperty("price_monthly").GetProperty("net").GetString()!, CultureInfo.InvariantCulture);
			var name = t.GetProperty("name").GetString()!;
			types.Add(new ServerType(
				name,
				t.GetProperty("description").GetString() ?? name.ToUpperInvariant(),
				t.GetProperty("cores").GetDouble(),
				(int)Math.Round(t.GetProperty("memory").GetDouble() * 1024),
				t.GetProperty("disk").GetInt32(),
				monthly,
				Architecture: t.TryGetProperty("architecture", out var arch) ? arch.GetString() ?? "x86" : "x86",
				Regions: prices.Select(p => p.GetProperty("location").GetString()!).ToList()));
		}
		return new ProviderPricing(provider, types, "EUR", 0.20m, Live: true, Today, "api.hetzner.cloud/v1/server_types", ["Prices exclude VAT; primary IPv4 billed separately where applicable."]);
	}

	async Task<ProviderPricing> DigitalOcean(ProviderInfo provider, string token, CancellationToken ct)
	{
		using var doc = await GetJson("https://api.digitalocean.com/v2/sizes?per_page=200", token, ct);
		var types = new List<ServerType>();
		foreach (var s in doc.RootElement.GetProperty("sizes").EnumerateArray())
		{
			var slug = s.GetProperty("slug").GetString()!;
			if (!slug.StartsWith("s-", StringComparison.Ordinal) || slug.Contains("-amd", StringComparison.Ordinal) || slug.Contains("-intel", StringComparison.Ordinal) || !s.GetProperty("available").GetBoolean())
			{
				continue;
			}
			types.Add(new ServerType(
				slug,
				s.TryGetProperty("description", out var d) && d.GetString() is { Length: > 0 } desc ? desc : slug,
				s.GetProperty("vcpus").GetInt32(),
				s.GetProperty("memory").GetInt32(),
				s.GetProperty("disk").GetInt32(),
				s.GetProperty("price_monthly").GetDecimal(),
				Regions: s.GetProperty("regions").EnumerateArray().Select(r => r.GetString()!).ToList()));
		}
		return new ProviderPricing(provider, types, "USD", 0.20m, Live: true, Today, "api.digitalocean.com/v2/sizes", ["Backups are weekly (20% of the Droplet price)."]);
	}
}
