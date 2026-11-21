using System.Security.Claims;
using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages;

[EnableRateLimiting("login")]
public sealed class SetupModel(JfriDb db, SetupToken setupToken, IPasswordHasher<AdminUser> hasher) : PageModel
{
	[BindProperty]
	public string? Token { get; set; }

	[BindProperty]
	public string? UserName { get; set; }

	[BindProperty]
	public string? Password { get; set; }

	public string? Error { get; private set; }

	public async Task<IActionResult> OnGetAsync() => await db.Admins.AnyAsync() ? NotFound() : Page();

	public async Task<IActionResult> OnPostAsync()
	{
		if (await db.Admins.AnyAsync())
		{
			return NotFound();
		}
		if (!setupToken.Matches(Token))
		{
			Error = "That setup token doesn't match. Check Jfri's startup log.";
			return Page();
		}
		var name = UserName?.Trim() ?? "";
		if (name.Length is < 2 or > 64)
		{
			Error = "Choose a username between 2 and 64 characters.";
			return Page();
		}
		if (Password is null || Password.Length < 12)
		{
			Error = "Use a password of at least 12 characters.";
			return Page();
		}
		var admin = new AdminUser { UserName = name, PasswordHash = "" };
		admin.PasswordHash = hasher.HashPassword(admin, Password);
		db.Admins.Add(admin);
		await db.SaveChangesAsync();
		setupToken.Consume();
		await SignIn(HttpContext, admin);
		TempData["Message"] = "You're set up. Paste a repository URL to deploy it.";
		return Redirect("/");
	}

	internal static Task SignIn(HttpContext context, AdminUser admin)
	{
		var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, admin.UserName), new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))], CookieAuthenticationDefaults.AuthenticationScheme);
		return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
	}
}
