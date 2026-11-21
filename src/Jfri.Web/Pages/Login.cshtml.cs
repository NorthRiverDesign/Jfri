using Jfri.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages;

[EnableRateLimiting("login")]
public sealed class LoginModel(JfriDb db, IPasswordHasher<AdminUser> hasher) : PageModel
{
	[BindProperty(SupportsGet = true)]
	public string? ReturnUrl { get; set; }

	[BindProperty]
	public string? UserName { get; set; }

	[BindProperty]
	public string? Password { get; set; }

	public string? Error { get; private set; }

	public void OnGet()
	{
	}

	public async Task<IActionResult> OnPostAsync()
	{
		var admin = await db.Admins.FirstOrDefaultAsync(a => a.UserName == (UserName ?? "").Trim());
		var result = admin is null || Password is null
			? PasswordVerificationResult.Failed
			: hasher.VerifyHashedPassword(admin, admin.PasswordHash, Password);
		if (admin is null || result == PasswordVerificationResult.Failed)
		{
			Error = "Wrong username or password.";
			return Page();
		}
		if (result == PasswordVerificationResult.SuccessRehashNeeded)
		{
			admin.PasswordHash = hasher.HashPassword(admin, Password!);
			await db.SaveChangesAsync();
		}
		await SetupModel.SignIn(HttpContext, admin);
		return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
	}
}
