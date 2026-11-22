using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages.Deployments;

public sealed class DetailsModel(JfriDb db, DeploymentScheduler scheduler) : PageModel
{
	public Deployment Deployment { get; private set; } = null!;
	public App App { get; private set; } = null!;
	/// <summary>This build is still on the app's server and isn't the live one.</summary>
	public bool CanRollBack { get; private set; }

	public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
	{
		var deployment = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
		if (deployment is null)
		{
			return NotFound();
		}
		Deployment = deployment;
		App = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == deployment.AppId, ct);
		CanRollBack = await scheduler.CanRollBackToAsync(App, deployment, ct);
		return Page();
	}

	public async Task<IActionResult> OnPostRollbackAsync(int id, CancellationToken ct)
	{
		var deployment = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
		if (deployment is null)
		{
			return NotFound();
		}
		var app = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == deployment.AppId, ct);
		if (!await scheduler.CanRollBackToAsync(app, deployment, ct))
		{
			TempData["Message"] = "That build can't be put back: it isn't on the app's current server, or it's already live.";
			return Redirect($"/deployments/{id}");
		}
		var rollback = await scheduler.QueueRollbackAsync(deployment, ct);
		return Redirect($"/deployments/{rollback.Id}");
	}
}
