using Jfri.Core.Planning;

namespace Jfri.Core.Pricing;

/// <summary>Picks the cheapest server that comfortably fits the plan on each provider and prices it.</summary>
public static class CostEstimator
{
	public static List<CostQuote> Estimate(DeploymentPlan plan, IEnumerable<ProviderPricing> pricing)
	{
		var needsX86 = plan.Services.Any(s => s.Kind == Analysis.ServiceKind.SqlServer);
		var quotes = new List<CostQuote>();
		foreach (var p in pricing)
		{
			var quote = Quote(plan.Resources, plan.Backups.ProviderBackups, needsX86, p);
			if (quote is not null)
			{
				quotes.Add(quote);
			}
		}
		return [.. quotes.OrderBy(q => q.TotalUsd)];
	}

	public static CostQuote? Quote(ResourceEstimate need, bool providerBackups, bool needsX86, ProviderPricing pricing)
	{
		var candidates = pricing.ServerTypes
			.Where(t => !needsX86 || t.Architecture == "x86")
			.Where(t => t.Regions is null || t.Regions.Count == 0 || t.Regions.Contains(pricing.Provider.DefaultRegion))
			.OrderBy(t => t.MonthlyPrice)
			.ThenByDescending(t => t.MemoryMb)
			.ToList();
		var recommended = candidates.FirstOrDefault(t => Fits(t, need.VCpu, need.MemoryMb, need.DiskGb));
		if (recommended is null)
		{
			return null;
		}
		var minimum = candidates.FirstOrDefault(t => t.MonthlyPrice < recommended.MonthlyPrice && Fits(t, 1, need.MinimumMemoryMb, need.DiskGb));
		var lines = new List<CostLine> { new($"{recommended.Name}: {recommended.VCpu:0.#} vCPU, {recommended.MemoryMb / 1024.0:0.#} GB RAM, {recommended.DiskGb} GB disk", recommended.MonthlyPrice) };
		if (providerBackups)
		{
			var backup = recommended.BackupMonthlyPrice ?? Math.Round(recommended.MonthlyPrice * pricing.BackupFraction, 2);
			lines.Add(new("Provider backups (whole-server)", backup));
		}
		var total = lines.Sum(l => l.Monthly);
		var notes = new List<string>(pricing.Notes);
		if (recommended.Architecture == "arm")
		{
			notes.Add("Arm64 server: every base image Jfri uses is multi-arch.");
		}
		return new CostQuote(
			pricing.Provider,
			recommended,
			pricing.Provider.DefaultRegion,
			lines,
			total,
			pricing.Currency,
			pricing.Currency == "EUR" ? Math.Round(total * ProviderCatalog.ApproxUsdPerEur, 2) : total,
			pricing.Live,
			pricing.AsOf,
			notes,
			minimum);
	}

	static bool Fits(ServerType t, double vcpu, int memoryMb, int diskGb) =>
		t.VCpu >= vcpu && t.MemoryMb >= memoryMb * 0.97 && t.DiskGb >= diskGb;
}
