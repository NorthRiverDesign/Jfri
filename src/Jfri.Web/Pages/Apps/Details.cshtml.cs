using System.Text.Json;
using Jfri.Core.Analysis;
using Jfri.Core.Planning;
using Jfri.Core.Pricing;
using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages.Apps;

public sealed class DetailsModel(JfriDb db, AppService apps) : PageModel
{
	public App App { get; private set; } = null!;
	public RepositoryProfile Profile { get; private set; } = new();
	public PlanOptions Options { get; private set; } = new();
	public DeploymentPlan? Plan { get; private set; }
	public List<CostQuote> Quotes { get; private set; } = [];
	public List<Deployment> Deployments { get; private set; } = [];
	public string WebhookUrl { get; private set; } = "";
	public string WebhookSecret { get; private set; } = "";
	public string? Error { get; private set; }

	public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
	{
		if (!await LoadAsync(id, ct))
		{
			return NotFound();
		}
		return Page();
	}

	public async Task<IActionResult> OnPostAnalyzeAsync(int id, CancellationToken ct)
	{
		var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct);
		if (app is null)
		{
			return NotFound();
		}
		Error = await apps.AnalyzeAsync(app, ct);
		if (Error is null)
		{
			TempData["Message"] = "Re-analyzed the latest commit.";
			return Redirect($"/apps/{id}");
		}
		await LoadAsync(id, ct);
		return Page();
	}

	public async Task<IActionResult> OnPostOptionsAsync(int id, string? runtime, string? domain, string? acmeEmail, int? databaseStorageGb, bool providerBackups, string? extraEnvironment, CancellationToken ct)
	{
		var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct);
		if (app is null)
		{
			return NotFound();
		}
		var options = AppService.Options(app);
		options.Runtime = runtime == nameof(RuntimeKind.Compose) ? RuntimeKind.Compose : RuntimeKind.Kubernetes;
		domain = domain?.Trim().ToLowerInvariant();
		if (!string.IsNullOrEmpty(domain) && (Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.')))
		{
			Error = "That domain doesn't look like a hostname (e.g. app.example.com).";
			await LoadAsync(id, ct);
			return Page();
		}
		options.Domain = string.IsNullOrEmpty(domain) ? null : domain;
		options.AcmeEmail = string.IsNullOrWhiteSpace(acmeEmail) || !acmeEmail.Contains('@') ? null : acmeEmail.Trim();
		options.DatabaseStorageGb = Math.Clamp(databaseStorageGb ?? options.DatabaseStorageGb, 1, 500);
		options.ProviderBackups = providerBackups;
		options.ExtraEnvironment = ParseEnvironment(extraEnvironment);
		app.OptionsJson = JsonSerializer.Serialize(options, AppService.Json);
		await db.SaveChangesAsync(ct);
		TempData["Message"] = "Plan updated.";
		return Redirect($"/apps/{id}");
	}

	public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
	{
		var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct);
		if (app is not null)
		{
			await apps.DeleteAsync(app, ct);
			TempData["Message"] = $"Removed {app.Name}.";
		}
		return Redirect("/");
	}

	static Dictionary<string, string> ParseEnvironment(string? text)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var raw in (text ?? "").Split('\n'))
		{
			var line = raw.Trim();
			var eq = line.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			var key = line[..eq].Trim();
			if (key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.') && !char.IsAsciiDigit(key[0]))
			{
				result[key] = line[(eq + 1)..].Trim();
			}
		}
		return result;
	}

	async Task<bool> LoadAsync(int id, CancellationToken ct)
	{
		var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
		if (app is null)
		{
			return false;
		}
		App = app;
		Profile = AppService.Profile(app);
		Options = AppService.Options(app);
		Plan = AppService.Plan(app);
		if (Plan is not null)
		{
			Quotes = await apps.EstimateAsync(Plan, ct);
		}
		Deployments = await db.Deployments.AsNoTracking().Where(d => d.AppId == id).OrderByDescending(d => d.Id).Take(10).ToListAsync(ct);
		WebhookUrl = $"{Request.Scheme}://{Request.Host}/hooks/apps/{id}/deploy";
		WebhookSecret = apps.WebhookSecret(app);
		return true;
	}
}
