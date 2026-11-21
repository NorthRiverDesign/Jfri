using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages;

public sealed class IndexModel(JfriDb db, AppService apps) : PageModel
{
	public sealed record AppRow(App App, string Summary, string Status);

	public List<AppRow> Apps { get; private set; } = [];

	[BindProperty]
	public string? RepoUrl { get; set; }

	[BindProperty]
	public string? Branch { get; set; }

	[BindProperty]
	public string? Token { get; set; }

	public string? Error { get; private set; }

	public async Task OnGetAsync() => await LoadAsync();

	public async Task<IActionResult> OnPostConnectAsync(CancellationToken ct)
	{
		var (app, error) = await apps.ConnectAsync(RepoUrl ?? "", Branch, Token, ct);
		if (app is null)
		{
			Error = error;
			await LoadAsync();
			return Page();
		}
		return Redirect($"/apps/{app.Id}");
	}

	async Task LoadAsync()
	{
		var all = await db.Apps.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
		var latest = await db.Deployments.AsNoTracking()
			.OrderByDescending(d => d.Id)
			.Select(d => new { d.AppId, d.Status })
			.Take(500)
			.ToListAsync();
		Apps = all.Select(a =>
		{
			var profile = AppService.Profile(a);
			var summary = profile.App is { } p ? $"{p.Framework} · {p.Stack} {p.RuntimeVersion}" : "not deployable yet";
			var status = latest.FirstOrDefault(d => d.AppId == a.Id)?.Status ?? "new";
			return new AppRow(a, summary, status);
		}).ToList();
	}
}
