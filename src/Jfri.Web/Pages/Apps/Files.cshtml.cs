using System.IO.Compression;
using System.Text;
using Jfri.Core.Generation;
using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages.Apps;

public sealed class FilesModel(JfriDb db) : PageModel
{
	public App App { get; private set; } = null!;
	public List<Artifact> Files { get; private set; } = [];

	public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct) => await LoadAsync(id, ct) ? Page() : NotFound();

	public async Task<IActionResult> OnGetDownloadAsync(int id, CancellationToken ct)
	{
		if (!await LoadAsync(id, ct))
		{
			return NotFound();
		}
		var buffer = new MemoryStream();
		using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var file in Files)
			{
				var entry = zip.CreateEntry(file.Path, CompressionLevel.Optimal);
				await using var stream = entry.Open();
				await stream.WriteAsync(Encoding.UTF8.GetBytes(file.Content), ct);
			}
		}
		buffer.Position = 0;
		return File(buffer, "application/zip", $"{App.Name}-jfri.zip");
	}

	async Task<bool> LoadAsync(int id, CancellationToken ct)
	{
		var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
		var plan = app is null ? null : AppService.Plan(app);
		if (app is null || plan is null)
		{
			return false;
		}
		App = app;
		Files = ArtifactBuilder.Build(plan, RenderContext.Preview(plan), app.Branch ?? "main");
		return true;
	}
}
