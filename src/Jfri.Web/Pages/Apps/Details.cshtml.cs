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

public sealed class DetailsModel(JfriDb db, AppService apps, Vault vault, DeploymentScheduler scheduler) : PageModel
{
	public App App { get; private set; } = null!;
	public RepositoryProfile Profile { get; private set; } = new();
	public PlanOptions Options { get; private set; } = new();
	public DeploymentPlan? Plan { get; private set; }
	public List<CostQuote> Quotes { get; private set; } = [];
	public List<Deployment> Deployments { get; private set; } = [];
	/// <summary>The server the app runs on, if it's deployed.</summary>
	public Server? Server { get; private set; }
	/// <summary>Names of the user's secret settings (values are never shown).</summary>
	public List<string> SecretNames { get; private set; } = [];
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

	/// <summary>Saves a secret setting. The value is encrypted and never rendered back; it applies on the next deploy.</summary>
	public async Task<IActionResult> OnPostSetSecretAsync(int id, string? name, string? value, CancellationToken ct)
	{
		var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct);
		if (app is null)
		{
			return NotFound();
		}
		name = name?.Trim() ?? "";
		value = value?.Trim() ?? "";
		Error = Planner.ManagedSecrets.Contains(name) ? $"Jfri generates {name} and the database or cache is set up with it, so it can't be replaced."
			: !Planner.IsSecretSettingName(name) ? "Secret names use capital letters, digits and underscores, like STRIPE_SECRET."
			: value.Length == 0 ? "Enter a value."
			: value.Contains('\n') || value.Contains('\r') ? "Values must be a single line. Base64-encode multi-line keys."
			: value.Length > 32 * 1024 ? "That value is too long (32 KB at most)."
			: null;
		if (Error is not null)
		{
			await LoadAsync(id, ct);
			return Page();
		}
		var values = vault.UnprotectMap(app.EncryptedUserSecrets);
		values[name] = value;
		app.EncryptedUserSecrets = vault.ProtectMap(values);
		var options = AppService.Options(app);
		if (!options.SecretSettings.Contains(name))
		{
			options.SecretSettings.Add(name);
		}
		options.ExtraEnvironment.Remove(name);
		app.OptionsJson = JsonSerializer.Serialize(options, AppService.Json);
		await db.SaveChangesAsync(ct);
		TempData["Message"] = $"Saved {name}. It applies on the next deploy.";
		return Redirect($"/apps/{id}#secrets");
	}

	public async Task<IActionResult> OnPostRemoveSecretAsync(int id, string? name, CancellationToken ct)
	{
		var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct);
		if (app is null)
		{
			return NotFound();
		}
		var values = vault.UnprotectMap(app.EncryptedUserSecrets);
		var options = AppService.Options(app);
		if (name is not null && (values.Remove(name) | options.SecretSettings.Remove(name)))
		{
			app.EncryptedUserSecrets = values.Count == 0 ? null : vault.ProtectMap(values);
			app.OptionsJson = JsonSerializer.Serialize(options, AppService.Json);
			await db.SaveChangesAsync(ct);
			TempData["Message"] = $"Removed {name}. It's gone from the server after the next deploy.";
		}
		return Redirect($"/apps/{id}#secrets");
	}

	/// <summary>Queues deleting the app from its server. Needs the app's name typed in, since it deletes the database.</summary>
	public async Task<IActionResult> OnPostTakeDownAsync(int id, string? confirmName, CancellationToken ct)
	{
		var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
		if (app is null)
		{
			return NotFound();
		}
		if (app.ServerId is null)
		{
			TempData["Message"] = $"{app.Name} isn't deployed anywhere.";
			return Redirect($"/apps/{id}");
		}
		if (!string.Equals(confirmName?.Trim(), app.Name, StringComparison.Ordinal))
		{
			Error = $"Type {app.Name} to confirm taking it down.";
			await LoadAsync(id, ct);
			return Page();
		}
		var teardown = await scheduler.QueueTeardownAsync(app, ct);
		return Redirect($"/deployments/{teardown.Id}");
	}

	public async Task<IActionResult> OnPostDeleteAsync(int id, string? confirmName, CancellationToken ct)
	{
		var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct);
		if (app is null)
		{
			return Redirect("/");
		}
		if (!string.Equals(confirmName?.Trim(), app.Name, StringComparison.Ordinal))
		{
			Error = $"Type {app.Name} to confirm removing it.";
		}
		else if (app.ServerId is not null)
		{
			// Jfri would lose track of what runs on the server: a leftover Compose proxy would still hold ports 80 and 443.
			Error = $"{app.Name} still runs on its server. Take it down first, or remove the server on the Connections page if it's gone.";
		}
		else if (await scheduler.BusyAsync(app.Id, ct))
		{
			Error = "A deployment for this app is queued or running. Wait for it to finish, then remove the app.";
		}
		if (Error is not null)
		{
			await LoadAsync(id, ct);
			return Page();
		}
		await apps.DeleteAsync(app, ct);
		TempData["Message"] = $"Removed {app.Name}.";
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
		SecretNames = Options.SecretSettings.Order(StringComparer.Ordinal).ToList();
		Plan = AppService.Plan(app);
		if (Plan is not null)
		{
			Quotes = await apps.EstimateAsync(Plan, ct);
		}
		Server = app.ServerId is null ? null : await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == app.ServerId, ct);
		Deployments = await db.Deployments.AsNoTracking().Where(d => d.AppId == id).OrderByDescending(d => d.Id).Take(10).ToListAsync(ct);
		WebhookUrl = $"{Request.Scheme}://{Request.Host}/hooks/apps/{id}/deploy";
		WebhookSecret = apps.WebhookSecret(app);
		return true;
	}
}
