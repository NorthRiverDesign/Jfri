using Jfri.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages.Deployments;

public sealed class DetailsModel(JfriDb db) : PageModel
{
	public Deployment Deployment { get; private set; } = null!;
	public App App { get; private set; } = null!;

	public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
	{
		var deployment = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
		if (deployment is null)
		{
			return NotFound();
		}
		Deployment = deployment;
		App = await db.Apps.AsNoTracking().FirstAsync(a => a.Id == deployment.AppId, ct);
		return Page();
	}
}
