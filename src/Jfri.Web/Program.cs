using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Jfri.Core.Pricing;
using Jfri.Core.Providers;
using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Never fall back to Kestrel's default port 5000; run on 5180 unless told otherwise.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["http_ports"]) && string.IsNullOrEmpty(builder.Configuration["https_ports"]))
{
	builder.WebHost.UseUrls("http://localhost:5180");
}

var paths = new JfriPaths(builder.Configuration["Jfri:DataDir"] ?? JfriPaths.Default());
builder.Services.AddSingleton(paths);
builder.Services.AddDbContext<JfriDb>(o => o.UseSqlite($"Data Source={paths.Database}"));
builder.Services.AddDataProtection()
	.SetApplicationName("Jfri")
	.PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));

builder.Services.AddSingleton<Vault>();
builder.Services.AddSingleton<SetupToken>();
builder.Services.AddSingleton<DeploymentLogs>();
builder.Services.AddSingleton<DeploymentQueue>();
builder.Services.AddScoped<DeploymentScheduler>();
builder.Services.AddScoped<AppService>();
builder.Services.AddHostedService<DeploymentWorker>();
builder.Services.AddHttpClient("providers", c =>
{
	c.Timeout = TimeSpan.FromSeconds(60);
	c.DefaultRequestHeaders.UserAgent.ParseAdd("Jfri/0.1");
});
builder.Services.AddSingleton(sp => new PricingClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("providers")));
builder.Services.AddSingleton<IEnumerable<IServerProvider>>(sp => ServerProviders.Create(sp.GetRequiredService<IHttpClientFactory>().CreateClient("providers")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
	.AddCookie(o =>
	{
		o.LoginPath = "/login";
		o.LogoutPath = "/logout";
		o.Cookie.Name = "jfri.auth";
		o.Cookie.HttpOnly = true;
		o.Cookie.SameSite = SameSiteMode.Strict;
		o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
		o.ExpireTimeSpan = TimeSpan.FromHours(12);
		o.SlidingExpiration = true;
	});
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery(o =>
{
	o.Cookie.Name = "jfri.af";
	o.Cookie.SameSite = SameSiteMode.Strict;
	o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(o =>
{
	o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
		context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
	o.AddPolicy("webhook", context => RateLimitPartition.GetFixedWindowLimiter(
		context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddRazorPages(o =>
{
	o.Conventions.AllowAnonymousToPage("/Login");
	o.Conventions.AllowAnonymousToPage("/Setup");
	o.Conventions.AllowAnonymousToPage("/Error");
});
builder.Services.AddScoped<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
if (builder.Configuration.GetValue<bool>("Jfri:TrustProxy") || Environment.GetEnvironmentVariable("JFRI_TRUST_PROXY") == "true")
{
	builder.Services.Configure<ForwardedHeadersOptions>(o =>
	{
		o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
		o.KnownIPNetworks.Clear();
		o.KnownProxies.Clear();
	});
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<JfriDb>();
	db.Database.Migrate();
	if (!db.Admins.Any())
	{
		var setup = scope.ServiceProvider.GetRequiredService<SetupToken>();
		var token = setup.Ensure();
		app.Logger.LogWarning("First run: open /setup and enter the setup token {Token} (also stored in {Path}).", token, setup.FilePath);
	}
}

if (builder.Configuration.GetValue<bool>("Jfri:TrustProxy") || Environment.GetEnvironmentVariable("JFRI_TRUST_PROXY") == "true")
{
	app.UseForwardedHeaders();
}
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error");
}
app.Use(async (context, next) =>
{
	var headers = context.Response.Headers;
	headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
	headers["X-Content-Type-Options"] = "nosniff";
	headers["X-Frame-Options"] = "DENY";
	headers["Referrer-Policy"] = "no-referrer";
	headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
	headers["Cross-Origin-Opener-Policy"] = "same-origin";
	if (context.Request.IsHttps)
	{
		headers["Strict-Transport-Security"] = "max-age=31536000";
	}
	await next();
});
app.UseStaticFiles();
// Until an admin exists, every page leads to first-run setup.
var adminExists = false;
app.Use(async (context, next) =>
{
	var path = context.Request.Path;
	if (!adminExists && !path.StartsWithSegments("/setup") && !path.StartsWithSegments("/css") && !path.StartsWithSegments("/js") && !path.StartsWithSegments("/hooks"))
	{
		var db = context.RequestServices.GetRequiredService<JfriDb>();
		adminExists = await db.Admins.AnyAsync();
		if (!adminExists)
		{
			context.Response.Redirect("/setup");
			return;
		}
	}
	await next();
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.MapGet("/api/deployments/{id:int}/log", async (int id, long? offset, JfriDb db, DeploymentLogs logs) =>
{
	var deployment = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
	if (deployment is null)
	{
		return Results.NotFound();
	}
	var (text, next) = logs.Read(id, offset ?? 0);
	return Results.Json(new { text, offset = next, status = deployment.Status, url = deployment.Url, error = deployment.Error });
});

// Push-to-deploy: GitHub webhooks (X-Hub-Signature-256) and Jfri's generated workflow use the same HMAC scheme.
app.MapPost("/hooks/apps/{id:int}/deploy", async (int id, HttpRequest request, JfriDb db, AppService apps, DeploymentScheduler scheduler, CancellationToken ct) =>
{
	if (request.ContentLength > 1024 * 1024)
	{
		return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
	}
	using var buffer = new MemoryStream();
	await request.Body.CopyToAsync(buffer, ct);
	var body = buffer.ToArray();
	var target = await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct);
	var signature = request.Headers["X-Hub-Signature-256"].ToString();
	if (target is null || !Webhooks.Verify(apps.WebhookSecret(target), body, signature))
	{
		return Results.Unauthorized();
	}
	if (request.Headers["X-GitHub-Event"].ToString() is "ping")
	{
		return Results.Ok(new { message = "pong" });
	}
	var gitRef = Webhooks.Ref(body);
	var branch = target.Branch ?? "main";
	if (gitRef is not null && gitRef != $"refs/heads/{branch}")
	{
		return Results.Accepted(value: new { message = $"Ignored {gitRef}; this app deploys {branch}." });
	}
	if (target.ServerId is null)
	{
		return Results.Conflict(new { message = "Deploy the app once from Jfri first, so it knows which server to use." });
	}
	var deployment = await scheduler.QueueAsync(target, target.ServerId, null, "webhook", ct);
	return Results.Accepted(value: new { deployment = deployment.Id });
}).AllowAnonymous().RequireRateLimiting("webhook").DisableAntiforgery();

app.Run();

/// <summary>HMAC-SHA256 webhook verification (GitHub's X-Hub-Signature-256 format).</summary>
public static class Webhooks
{
	public static bool Verify(string secret, byte[] body, string? signatureHeader)
	{
		if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal))
		{
			return false;
		}
		byte[] provided;
		try
		{
			provided = Convert.FromHexString(signatureHeader["sha256=".Length..]);
		}
		catch (FormatException)
		{
			return false;
		}
		var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
		return CryptographicOperations.FixedTimeEquals(expected, provided);
	}

	public static string Sign(string secret, byte[] body) => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

	public static string? Ref(byte[] body)
	{
		try
		{
			using var doc = JsonDocument.Parse(body);
			return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}

public partial class Program;
