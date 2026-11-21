namespace Jfri.Core.Pricing;

/// <summary>A hosting provider Jfri can price (and, for most, create servers on).</summary>
public sealed record ProviderInfo(string Id, string Name, string DefaultRegion, IReadOnlyList<RegionInfo> Regions, string TokenHelpUrl, bool PublicPricing);

public sealed record RegionInfo(string Id, string Name);

/// <summary>A server size and its monthly price.</summary>
public sealed record ServerType(
	string Id,
	string Name,
	double VCpu,
	int MemoryMb,
	int DiskGb,
	decimal MonthlyPrice,
	/// <summary>Backup add-on price per month, when the provider publishes one per size.</summary>
	decimal? BackupMonthlyPrice = null,
	string Architecture = "x86",
	IReadOnlyList<string>? Regions = null);

/// <summary>A provider's price list, either fetched live or from Jfri's dated catalog.</summary>
public sealed record ProviderPricing(
	ProviderInfo Provider,
	IReadOnlyList<ServerType> ServerTypes,
	string Currency,
	/// <summary>Backup price as a fraction of the server price, when not published per size.</summary>
	decimal BackupFraction,
	bool Live,
	string AsOf,
	string Source,
	IReadOnlyList<string> Notes);

public sealed record CostLine(string Label, decimal Monthly);

/// <summary>What running the plan on one provider costs per month.</summary>
public sealed record CostQuote(
	ProviderInfo Provider,
	ServerType Server,
	string Region,
	IReadOnlyList<CostLine> Lines,
	decimal Total,
	string Currency,
	/// <summary>Total converted to USD for ranking (approximate for non-USD prices).</summary>
	decimal TotalUsd,
	bool Live,
	string AsOf,
	IReadOnlyList<string> Notes,
	/// <summary>A cheaper size that still meets the minimum requirements, if any.</summary>
	ServerType? Minimum);
