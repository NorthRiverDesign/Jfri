using System.Net;
using System.Text.Json;
using Jfri.Core.Analysis;
using Jfri.Core.Planning;
using Jfri.Core.Pricing;
using Jfri.Core.Providers;

namespace Jfri.Core.Tests;

public sealed class CostEstimatorTests
{
	static ProviderPricing Pricing(string currency, params ServerType[] types) =>
		new(ProviderCatalog.Vultr, types, currency, 0.20m, Live: true, "2026-11-21", "test", []);

	[Fact]
	public void Picks_the_cheapest_server_that_fits_and_adds_backups()
	{
		var need = new ResourceEstimate(2, 3000, 50, 1800);
		var quote = CostEstimator.Quote(need, providerBackups: true, needsX86: false, Pricing("USD",
			new ServerType("small", "Small", 1, 2048, 50, 10m),
			new ServerType("medium", "Medium", 2, 4096, 80, 20m),
			new ServerType("large", "Large", 4, 8192, 160, 40m)))!;

		Assert.Equal("medium", quote.Server.Id);
		Assert.Equal(24m, quote.Total);
		Assert.Equal(2, quote.Lines.Count);
		// "small" can't run it comfortably, but it meets the bare minimum, so it's offered as a tight fit.
		Assert.Equal("small", quote.Minimum!.Id);
	}

	[Fact]
	public void Reports_a_tight_fit_when_a_cheaper_size_meets_the_minimum()
	{
		var need = new ResourceEstimate(1, 3000, 40, 1800);
		var quote = CostEstimator.Quote(need, false, false, Pricing("USD",
			new ServerType("small", "Small", 1, 2048, 50, 10m),
			new ServerType("medium", "Medium", 2, 4096, 80, 20m)))!;

		Assert.Equal("medium", quote.Server.Id);
		Assert.Equal("small", quote.Minimum!.Id);
		Assert.Equal(20m, quote.Total);
	}

	[Fact]
	public void Per_size_backup_prices_and_euro_ranking()
	{
		var linode = CostEstimator.Quote(new ResourceEstimate(1, 900, 20, 900), true, false,
			ProviderCatalog.Fallback(ProviderCatalog.Linode))!;
		Assert.Equal("g6-nanode-1", linode.Server.Id);
		Assert.Equal(7m, linode.Total);

		var hetzner = CostEstimator.Quote(new ResourceEstimate(2, 3500, 50, 2000), false, false,
			ProviderCatalog.Fallback(ProviderCatalog.Hetzner))!;
		Assert.Equal("EUR", hetzner.Currency);
		Assert.Equal(Math.Round(hetzner.Total * ProviderCatalog.ApproxUsdPerEur, 2), hetzner.TotalUsd);
		Assert.False(hetzner.Live);
	}

	[Fact]
	public void Sql_server_plans_skip_arm_servers()
	{
		var pricing = Pricing("EUR",
			new ServerType("cax21", "CAX21", 4, 8192, 80, 7m, Architecture: "arm"),
			new ServerType("cpx32", "CPX32", 4, 8192, 160, 15m));
		Assert.Equal("cax21", CostEstimator.Quote(new ResourceEstimate(2, 6000, 50, 4000), false, false, pricing)!.Server.Id);
		Assert.Equal("cpx32", CostEstimator.Quote(new ResourceEstimate(2, 6000, 50, 4000), false, true, pricing)!.Server.Id);
	}

	[Fact]
	public void Estimates_every_provider_for_a_real_plan_sorted_by_price()
	{
		var plan = Fixtures.Plan("laravel-mysql");
		var quotes = CostEstimator.Estimate(plan, ProviderCatalog.All.Select(ProviderCatalog.Fallback));

		Assert.Equal(4, quotes.Count);
		Assert.Equal(quotes.OrderBy(q => q.TotalUsd).Select(q => q.Provider.Id), quotes.Select(q => q.Provider.Id));
		Assert.All(quotes, q => Assert.True(q.Server.MemoryMb >= plan.Resources.MemoryMb * 0.97));
	}
}

public sealed class PricingClientTests
{
	[Fact]
	public async Task Parses_hetzner_server_types_and_skips_deprecated_ones()
	{
		var json = JsonSerializer.Serialize(new
		{
			server_types = new object[]
			{
				new { name = "cpx22", description = "CPX22", cores = 2, memory = 4.0, disk = 80, cpu_type = "shared", architecture = "x86", deprecation = (object?)null, prices = new[] { new { location = "nbg1", price_monthly = new { net = "19.9900", gross = "23.7881" } } } },
				new { name = "cx22", description = "CX22", cores = 2, memory = 4.0, disk = 40, cpu_type = "shared", architecture = "x86", deprecation = (object?)new { announced = "2026-01-01" }, prices = new[] { new { location = "nbg1", price_monthly = new { net = "3.7900", gross = "4.5101" } } } },
				new { name = "ccx13", description = "CCX13", cores = 2, memory = 8.0, disk = 80, cpu_type = "dedicated", architecture = "x86", deprecation = (object?)null, prices = new[] { new { location = "nbg1", price_monthly = new { net = "14.0000", gross = "16.6600" } } } },
			},
		});
		var handler = new FakeHttpHandler((_, _) => (HttpStatusCode.OK, json));
		var pricing = await new PricingClient(new HttpClient(handler)).GetAsync(ProviderCatalog.Hetzner, "token-123", CancellationToken.None);

		Assert.True(pricing.Live);
		var type = Assert.Single(pricing.ServerTypes);
		Assert.Equal("cpx22", type.Id);
		Assert.Equal(4096, type.MemoryMb);
		Assert.Equal(19.99m, type.MonthlyPrice);
		Assert.Equal("Bearer token-123", handler.Requests[0].Authorization);
	}

	[Fact]
	public async Task Parses_vultr_and_linode_public_catalogs()
	{
		var vultr = JsonSerializer.Serialize(new
		{
			plans = new object[]
			{
				new { id = "vc2-1c-0.5gb-free", vcpu_count = 1, ram = 512, disk = 10, monthly_cost = 0m, locations = new[] { "ewr" } },
				new { id = "vc2-2c-4gb", vcpu_count = 2, ram = 4096, disk = 80, monthly_cost = 20m, locations = new[] { "ewr", "ord", "lax", "ams", "fra" } },
			},
		});
		var linode = JsonSerializer.Serialize(new
		{
			data = new object[]
			{
				new { id = "g6-standard-2", label = "Linode 4GB", @class = "standard", vcpus = 2, memory = 4096, disk = 81920, price = new { monthly = 24.0m }, addons = new { backups = new { price = new { monthly = 5.0m } } } },
				new { id = "g7-highmem-1", label = "Linode 24GB", @class = "highmem", vcpus = 2, memory = 24576, disk = 20480, price = new { monthly = 60.0m }, addons = new { backups = new { price = new { monthly = 5.0m } } } },
			},
		});
		var handler = new FakeHttpHandler((request, _) => (HttpStatusCode.OK, request.RequestUri!.Host.Contains("vultr", StringComparison.Ordinal) ? vultr : linode));
		var client = new PricingClient(new HttpClient(handler));

		var v = await client.GetAsync(ProviderCatalog.Vultr, null, CancellationToken.None);
		Assert.Equal(["vc2-2c-4gb"], v.ServerTypes.Select(t => t.Id));
		var l = await client.GetAsync(ProviderCatalog.Linode, null, CancellationToken.None);
		var standard = Assert.Single(l.ServerTypes);
		Assert.Equal(80, standard.DiskGb);
		Assert.Equal(5m, standard.BackupMonthlyPrice);
		Assert.All(handler.Requests, r => Assert.Null(r.Authorization));
	}

	[Fact]
	public async Task Falls_back_to_the_catalog_when_the_api_fails_or_no_token_is_set()
	{
		var handler = new FakeHttpHandler((_, _) => (HttpStatusCode.InternalServerError, "{}"));
		var client = new PricingClient(new HttpClient(handler));

		var vultr = await client.GetAsync(ProviderCatalog.Vultr, null, CancellationToken.None);
		Assert.False(vultr.Live);
		Assert.Contains(vultr.Notes, n => n.Contains("Live prices unavailable", StringComparison.Ordinal));

		var digitalOcean = await client.GetAsync(ProviderCatalog.DigitalOcean, null, CancellationToken.None);
		Assert.False(digitalOcean.Live);
		Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("digitalocean", StringComparison.Ordinal));
	}
}

public sealed class ProviderTests
{
	[Fact]
	public async Task Hetzner_registers_the_key_then_creates_an_ubuntu_server_with_backups()
	{
		var handler = new FakeHttpHandler((request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/v1/ssh_keys" => (HttpStatusCode.Created, """{"ssh_key":{"id":42,"name":"k"}}"""),
			"/v1/servers" => (HttpStatusCode.Created, """{"server":{"id":7,"status":"initializing","public_net":{"ipv4":{"ip":"203.0.113.5"}}}}"""),
			_ => (HttpStatusCode.Created, """{"action":{"id":1}}"""),
		});
		var server = await new HetznerProvider(new HttpClient(handler)).CreateAsync("tok", new CreateServerRequest("jfri-shop-abc", "nbg1", "cpx22", "ssh-ed25519 AAAA test", Backups: true), CancellationToken.None);

		Assert.Equal("7", server.Id);
		Assert.Equal("203.0.113.5", server.Ipv4);
		Assert.False(server.Running);
		Assert.Equal(["/v1/ssh_keys", "/v1/servers", "/v1/servers/7/actions/enable_backup"], handler.Requests.Select(r => new Uri(r.Url).AbsolutePath));
		using var body = JsonDocument.Parse(handler.Requests[1].Body!);
		Assert.Equal("ubuntu-24.04", body.RootElement.GetProperty("image").GetString());
		Assert.Equal("cpx22", body.RootElement.GetProperty("server_type").GetString());
		Assert.Equal("42", body.RootElement.GetProperty("ssh_keys")[0].GetString());
		Assert.All(handler.Requests, r => Assert.Equal("Bearer tok", r.Authorization));
	}

	[Fact]
	public async Task DigitalOcean_reads_the_public_ip_from_networks()
	{
		var handler = new FakeHttpHandler((_, _) => (HttpStatusCode.OK, """{"droplet":{"id":99,"status":"active","networks":{"v4":[{"ip_address":"10.0.0.2","type":"private"},{"ip_address":"198.51.100.9","type":"public"}]}}}"""));
		var server = await new DigitalOceanProvider(new HttpClient(handler)).GetAsync("tok", "99", CancellationToken.None);

		Assert.Equal("198.51.100.9", server.Ipv4);
		Assert.True(server.Running);
	}

	[Fact]
	public async Task Linode_passes_the_key_inline_and_vultr_waits_for_a_real_ip()
	{
		var linodeHandler = new FakeHttpHandler((_, _) => (HttpStatusCode.OK, """{"id":5,"status":"provisioning","ipv4":["192.0.2.1"]}"""));
		await new LinodeProvider(new HttpClient(linodeHandler)).CreateAsync("tok", new CreateServerRequest("jfri-a-1", "us-east", "g6-standard-2", "ssh-ed25519 AAAA", false), CancellationToken.None);
		using var linodeBody = JsonDocument.Parse(linodeHandler.Requests.Single().Body!);
		Assert.Equal("ssh-ed25519 AAAA", linodeBody.RootElement.GetProperty("authorized_keys")[0].GetString());
		Assert.Equal("linode/ubuntu24.04", linodeBody.RootElement.GetProperty("image").GetString());

		var vultrHandler = new FakeHttpHandler((_, _) => (HttpStatusCode.OK, """{"instance":{"id":"abc","main_ip":"0.0.0.0","status":"pending","power_status":"stopped"}}"""));
		var pending = await new VultrProvider(new HttpClient(vultrHandler)).GetAsync("tok", "abc", CancellationToken.None);
		Assert.Null(pending.Ipv4);
		Assert.False(pending.Running);
	}

	[Fact]
	public async Task Api_errors_surface_the_provider_message()
	{
		var handler = new FakeHttpHandler((_, _) => (HttpStatusCode.Forbidden, """{"error":{"code":"forbidden","message":"insufficient permissions"}}"""));
		var error = await Assert.ThrowsAsync<ProviderApiException>(() => new HetznerProvider(new HttpClient(handler)).GetAsync("tok", "1", CancellationToken.None));
		Assert.Contains("insufficient permissions", error.Message);
	}

	[Fact]
	public void Server_names_are_hostname_safe() =>
		Assert.Matches("^jfri-my-app-[0-9a-f]{6}$", ServerProviders.ServerName("my-app"));
}
