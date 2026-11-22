using System.Text.Json;
using Jfri.Core.Analysis;
using Jfri.Core.Generation;
using Jfri.Core.Planning;
using Jfri.Core.Pricing;
using Jfri.Core.Sources;
using Jfri.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Services;

/// <summary>Connects repositories, analyzes them, and turns stored apps into plans and cost estimates.</summary>
public sealed class AppService(JfriDb db, Vault vault, JfriPaths paths, PricingClient pricing, ILogger<AppService> logger)
{
	public static readonly JsonSerializerOptions Json = ArtifactBuilder.JsonOptions;

	/// <summary>Clones and analyzes a repository, creating the app. Returns the app or an error message.</summary>
	public async Task<(App? App, string? Error)> ConnectAsync(string repoUrl, string? branch, string? token, CancellationToken ct)
	{
		repoUrl = repoUrl.Trim();
		if (GitRepository.ValidateUrl(repoUrl) is { } urlError)
		{
			return (null, urlError);
		}
		branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
		if (!GitRepository.IsValidBranch(branch))
		{
			return (null, "That branch name isn't valid.");
		}
		var name = await UniqueNameAsync(Planner.Slug(GitRepository.NameFromUrl(repoUrl)), ct);
		var app = new App
		{
			Name = name,
			RepoUrl = repoUrl,
			Branch = branch,
			EncryptedGitToken = string.IsNullOrWhiteSpace(token) ? null : vault.Protect(token.Trim()),
			ProfileJson = "{}",
			OptionsJson = JsonSerializer.Serialize(new PlanOptions(), Json),
			EncryptedWebhookSecret = vault.Protect(Vault.NewWebhookSecret()),
		};
		db.Apps.Add(app);
		await db.SaveChangesAsync(ct);
		var error = await AnalyzeAsync(app, ct);
		if (error is not null)
		{
			db.Apps.Remove(app);
			await db.SaveChangesAsync(ct);
			return (null, error);
		}
		return (app, null);
	}

	/// <summary>Fresh clone + analysis. Returns an error message on failure.</summary>
	public async Task<string?> AnalyzeAsync(App app, CancellationToken ct)
	{
		var checkout = paths.Workspace(app.Id);
		try
		{
			await GitRepository.CloneAsync(app.RepoUrl, app.Branch, GitToken(app), checkout, null, ct);
			app.Commit = await GitRepository.HeadAsync(checkout, ct);
			var profile = RepositoryAnalyzer.Analyze(checkout, GitRepository.NameFromUrl(app.RepoUrl));
			app.ProfileJson = JsonSerializer.Serialize(profile, Json);
			app.AnalyzedAt = DateTime.UtcNow;
			await db.SaveChangesAsync(ct);
			return null;
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or IOException)
		{
			logger.LogWarning("Analysis of app {App} failed: {Message}", app.Id, ex.Message);
			return ex.Message;
		}
	}

	public string? GitToken(App app) => app.EncryptedGitToken is null ? null : vault.Unprotect(app.EncryptedGitToken);

	public static RepositoryProfile Profile(App app) => JsonSerializer.Deserialize<RepositoryProfile>(app.ProfileJson, Json) ?? new RepositoryProfile();

	public static PlanOptions Options(App app) => JsonSerializer.Deserialize<PlanOptions>(app.OptionsJson, Json) ?? new PlanOptions();

	public static DeploymentPlan? Plan(App app)
	{
		var profile = Profile(app);
		return profile.IsDeployable ? Planner.Plan(profile, Options(app), app.Name) : null;
	}

	public async Task<List<CostQuote>> EstimateAsync(DeploymentPlan plan, CancellationToken ct)
	{
		var tokens = new Dictionary<string, string>();
		foreach (var connection in await db.Connections.AsNoTracking().ToListAsync(ct))
		{
			tokens.TryAdd(connection.Provider, vault.Unprotect(connection.EncryptedToken));
		}
		var prices = await pricing.GetAllAsync(tokens, ct);
		return CostEstimator.Estimate(plan, prices);
	}

	public async Task<ProviderPricing> PricingAsync(ProviderInfo provider, CancellationToken ct)
	{
		var connection = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Provider == provider.Id, ct);
		return await pricing.GetAsync(provider, connection is null ? null : vault.Unprotect(connection.EncryptedToken), ct);
	}

	public string WebhookSecret(App app) => vault.Unprotect(app.EncryptedWebhookSecret);

	public async Task DeleteAsync(App app, CancellationToken ct)
	{
		db.Apps.Remove(app);
		await db.SaveChangesAsync(ct);
		var checkout = paths.Workspace(app.Id);
		if (Directory.Exists(checkout))
		{
			Directory.Delete(checkout, recursive: true);
		}
	}

	async Task<string> UniqueNameAsync(string baseName, CancellationToken ct)
	{
		var name = baseName;
		for (var i = 2; await db.Apps.AnyAsync(a => a.Name == name, ct); i++)
		{
			// Leave room for the suffix: Slug truncates to 40 characters, which would cut it off again.
			var suffix = $"-{i}";
			name = Planner.Slug(baseName[..Math.Min(baseName.Length, 40 - suffix.Length)].TrimEnd('-') + suffix);
		}
		return name;
	}
}
