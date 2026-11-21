namespace Jfri.Core.Pricing;

/// <summary>
/// The providers Jfri knows, and a dated fallback price list used when live prices can't be
/// fetched (Hetzner and DigitalOcean need an API token to list prices; Vultr and Linode publish
/// theirs openly, so those are normally live). Update <see cref="CatalogDate"/> with the prices.
/// </summary>
public static class ProviderCatalog
{
	public const string CatalogDate = "2026-11";
	/// <summary>Only used to rank EUR quotes next to USD ones; displayed totals stay in their own currency.</summary>
	public const decimal ApproxUsdPerEur = 1.10m;

	public static readonly ProviderInfo Hetzner = new("hetzner", "Hetzner Cloud", "nbg1",
		[new("nbg1", "Nuremberg"), new("fsn1", "Falkenstein"), new("hel1", "Helsinki"), new("sin", "Singapore")],
		"https://console.hetzner.cloud/ → your project → Security → API tokens (Read & Write)", PublicPricing: false);

	public static readonly ProviderInfo DigitalOcean = new("digitalocean", "DigitalOcean", "nyc3",
		[new("nyc3", "New York"), new("sfo3", "San Francisco"), new("tor1", "Toronto"), new("ams3", "Amsterdam"), new("fra1", "Frankfurt"), new("lon1", "London"), new("sgp1", "Singapore"), new("syd1", "Sydney"), new("blr1", "Bangalore")],
		"https://cloud.digitalocean.com/account/api/tokens (scopes: droplet, ssh_key, sizes read)", PublicPricing: false);

	public static readonly ProviderInfo Vultr = new("vultr", "Vultr", "ewr",
		[new("ewr", "New Jersey"), new("ord", "Chicago"), new("dfw", "Dallas"), new("lax", "Los Angeles"), new("sea", "Seattle"), new("ams", "Amsterdam"), new("fra", "Frankfurt"), new("lhr", "London"), new("sgp", "Singapore"), new("nrt", "Tokyo"), new("syd", "Sydney")],
		"https://my.vultr.com/settings/#settingsapi (allow Jfri's IP in the access control list)", PublicPricing: true);

	public static readonly ProviderInfo Linode = new("linode", "Akamai Cloud (Linode)", "us-east",
		[new("us-east", "Newark"), new("us-ord", "Chicago"), new("us-west", "Fremont"), new("us-sea", "Seattle"), new("eu-central", "Frankfurt"), new("gb-lon", "London"), new("ap-south", "Singapore"), new("ap-northeast", "Tokyo")],
		"https://cloud.linode.com/profile/tokens (Linodes: read/write)", PublicPricing: true);

	public static IReadOnlyList<ProviderInfo> All { get; } = [Hetzner, DigitalOcean, Vultr, Linode];

	public static ProviderInfo? Find(string id) => All.FirstOrDefault(p => p.Id == id);

	/// <summary>Fallback price lists (shared-CPU plans, excluding tax).</summary>
	public static ProviderPricing Fallback(ProviderInfo provider) => provider.Id switch
	{
		"hetzner" => new(provider,
			[
				new("cpx02", "CPX02", 1, 1024, 20, 6.49m),
				new("cpx12", "CPX12", 1, 2048, 40, 11.99m),
				new("cpx22", "CPX22", 2, 4096, 80, 19.99m),
				new("cpx32", "CPX32", 4, 8192, 160, 35.99m),
				new("cpx42", "CPX42", 8, 16384, 320, 69.99m),
				new("cpx52", "CPX52", 12, 24576, 480, 100.99m),
				new("cpx62", "CPX62", 16, 32768, 640, 130.49m),
			],
			"EUR", 0.20m, Live: false, CatalogDate, "hetzner.com/cloud (Nuremberg/Falkenstein prices, IPv4 included)",
			["Prices exclude VAT. Add a Hetzner token in Connections for live prices in every location."]),
		"digitalocean" => new(provider,
			[
				new("s-1vcpu-512mb-10gb", "Basic 512 MB", 1, 512, 10, 4m),
				new("s-1vcpu-1gb", "Basic 1 GB", 1, 1024, 25, 6m),
				new("s-1vcpu-2gb", "Basic 2 GB", 1, 2048, 50, 12m),
				new("s-2vcpu-2gb", "Basic 2 vCPU / 2 GB", 2, 2048, 60, 18m),
				new("s-2vcpu-4gb", "Basic 4 GB", 2, 4096, 80, 24m),
				new("s-4vcpu-8gb", "Basic 8 GB", 4, 8192, 160, 48m),
				new("s-8vcpu-16gb", "Basic 16 GB", 8, 16384, 320, 96m),
			],
			"USD", 0.20m, Live: false, CatalogDate, "digitalocean.com/pricing/droplets",
			["Backups are weekly (20% of the Droplet price). Add a DigitalOcean token in Connections for live prices."]),
		"vultr" => new(provider,
			[
				new("vc2-1c-1gb", "Cloud Compute 1 GB", 1, 1024, 25, 5m),
				new("vc2-1c-2gb", "Cloud Compute 2 GB", 1, 2048, 55, 10m),
				new("vc2-2c-2gb", "Cloud Compute 2 vCPU / 2 GB", 2, 2048, 65, 15m),
				new("vc2-2c-4gb", "Cloud Compute 4 GB", 2, 4096, 80, 20m),
				new("vc2-4c-8gb", "Cloud Compute 8 GB", 4, 8192, 160, 40m),
				new("vc2-6c-16gb", "Cloud Compute 16 GB", 6, 16384, 320, 80m),
				new("vc2-8c-32gb", "Cloud Compute 32 GB", 8, 32768, 640, 160m),
			],
			"USD", 0.20m, Live: false, CatalogDate, "api.vultr.com/v2/plans",
			[]),
		"linode" => new(provider,
			[
				new("g6-nanode-1", "Nanode 1 GB", 1, 1024, 25, 5m, 2m),
				new("g6-standard-1", "Linode 2 GB", 1, 2048, 50, 12m, 2.5m),
				new("g6-standard-2", "Linode 4 GB", 2, 4096, 80, 24m, 5m),
				new("g6-standard-4", "Linode 8 GB", 4, 8192, 160, 48m, 10m),
				new("g6-standard-6", "Linode 16 GB", 6, 16384, 320, 96m, 20m),
				new("g6-standard-8", "Linode 32 GB", 8, 32768, 640, 192m, 40m),
			],
			"USD", 0.25m, Live: false, CatalogDate, "api.linode.com/v4/linode/types",
			[]),
		_ => throw new ArgumentException($"Unknown provider {provider.Id}"),
	};
}
