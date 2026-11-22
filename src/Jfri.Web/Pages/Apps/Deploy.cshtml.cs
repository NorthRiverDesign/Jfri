using Jfri.Core.Planning;
using Jfri.Core.Pricing;
using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages.Apps;

public sealed class DeployModel(JfriDb db, AppService apps, Vault vault, DeploymentScheduler scheduler) : PageModel
{
	public App App { get; private set; } = null!;
	public DeploymentPlan Plan { get; private set; } = null!;
	public ProviderInfo? Provider { get; private set; }
	public List<ServerType> Sizes { get; private set; } = [];
	public string Currency { get; private set; } = "USD";
	public string? SelectedSize { get; private set; }
	public List<Connection> Connections { get; private set; } = [];
	public List<Server> Servers { get; private set; } = [];
	/// <summary>The server this app already runs on (or was being set up on), if any.</summary>
	public Server? CurrentServer { get; private set; }
	/// <summary>Servers this app can't go on, with the reason.</summary>
	public Dictionary<int, string> Problems { get; private set; } = [];
	public string? Error { get; private set; }

	public async Task<IActionResult> OnGetAsync(int id, string? provider, string? size, CancellationToken ct) =>
		await LoadAsync(id, provider, size, ct) ? Page() : NotFound();

	public async Task<IActionResult> OnPostAsync(int id, string? mode, int? serverId, string? provider, string? size, string? region, int? connectionId, string? token, bool backups, CancellationToken ct)
	{
		if (!await LoadAsync(id, provider, size, ct))
		{
			return NotFound();
		}
		var app = await db.Apps.FirstAsync(a => a.Id == id, ct);
		if (mode == "existing")
		{
			var server = Servers.FirstOrDefault(s => s.Id == serverId);
			if (server is null)
			{
				Error = "Pick a server.";
				return Page();
			}
			if (Problems.TryGetValue(server.Id, out var problem))
			{
				Error = problem;
				return Page();
			}
			var queued = await scheduler.QueueAsync(app, server.Id, null, "manual", ct);
			return Redirect($"/deployments/{queued.Id}");
		}

		if (Provider is null || Sizes.All(s => s.Id != size) || Provider.Regions.All(r => r.Id != region))
		{
			Error = "Choose a provider, size and region.";
			return Page();
		}
		Connection? connection;
		if (connectionId is not null)
		{
			connection = Connections.FirstOrDefault(c => c.Id == connectionId);
		}
		else if (!string.IsNullOrWhiteSpace(token))
		{
			connection = new Connection
			{
				Provider = Provider.Id,
				Name = $"{Provider.Name} token",
				EncryptedToken = vault.Protect(token.Trim()),
				Hint = Vault.Hint(token.Trim()),
			};
			db.Connections.Add(connection);
			await db.SaveChangesAsync(ct);
		}
		else
		{
			connection = null;
		}
		if (connection is null)
		{
			Error = $"Add a {Provider.Name} API token.";
			return Page();
		}
		var deployment = await scheduler.QueueAsync(app, null, new NewServerSpec(Provider.Id, connection.Id, region!, size!, backups), "manual", ct);
		return Redirect($"/deployments/{deployment.Id}");
	}

	async Task<bool> LoadAsync(int id, string? provider, string? size, CancellationToken ct)
	{
		var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
		var plan = app is null ? null : AppService.Plan(app);
		if (app is null || plan is null)
		{
			return false;
		}
		App = app;
		Plan = plan;
		Servers = await db.Servers.AsNoTracking().Where(s => s.Status == ServerStatus.Ready && s.Host != "").OrderBy(s => s.Name).ToListAsync(ct);
		CurrentServer = Servers.FirstOrDefault(s => s.Id == app.ServerId);
		foreach (var server in Servers)
		{
			if (await ServerPlacement.ProblemAsync(db, app, plan, server, ct) is { } problem)
			{
				Problems[server.Id] = problem;
			}
		}
		Provider = provider is null ? null : ProviderCatalog.Find(provider);
		if (Provider is not null)
		{
			var pricing = await apps.PricingAsync(Provider, ct);
			Currency = pricing.Currency;
			Sizes = pricing.ServerTypes.OrderBy(t => t.MonthlyPrice).ToList();
			SelectedSize = size ?? Jfri.Core.Pricing.CostEstimator.Quote(plan.Resources, plan.Backups.ProviderBackups, false, pricing)?.Server.Id;
			Connections = await db.Connections.AsNoTracking().Where(c => c.Provider == Provider.Id).OrderBy(c => c.Name).ToListAsync(ct);
		}
		return true;
	}
}
