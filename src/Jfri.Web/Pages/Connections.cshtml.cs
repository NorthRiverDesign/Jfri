using System.Text.RegularExpressions;
using Jfri.Core.Execution;
using Jfri.Core.Pricing;
using Jfri.Core.Providers;
using Jfri.Web.Data;
using Jfri.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Pages;

public sealed partial class ConnectionsModel(JfriDb db, Vault vault, IEnumerable<IServerProvider> providers, ILogger<ConnectionsModel> logger) : PageModel
{
	public List<Connection> Connections { get; private set; } = [];
	public List<Server> Servers { get; private set; } = [];
	public string? Error { get; private set; }
	public string? NewPublicKey { get; private set; }

	public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

	public async Task<IActionResult> OnPostAddConnectionAsync(string? provider, string? name, string? token, CancellationToken ct)
	{
		var info = provider is null ? null : ProviderCatalog.Find(provider);
		if (info is null || string.IsNullOrWhiteSpace(token) || token.Trim().Length < 16)
		{
			Error = "Pick a provider and paste its API token.";
			await LoadAsync(ct);
			return Page();
		}
		token = token.Trim();
		db.Connections.Add(new Connection
		{
			Provider = info.Id,
			Name = string.IsNullOrWhiteSpace(name) ? $"{info.Name} token" : name.Trim(),
			EncryptedToken = vault.Protect(token),
			Hint = Vault.Hint(token),
		});
		await db.SaveChangesAsync(ct);
		TempData["Message"] = $"Saved your {info.Name} token.";
		return Redirect("/connections");
	}

	public async Task<IActionResult> OnPostDeleteConnectionAsync(int id, CancellationToken ct)
	{
		var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
		if (connection is not null)
		{
			db.Connections.Remove(connection);
			await db.SaveChangesAsync(ct);
		}
		return Redirect("/connections");
	}

	public async Task<IActionResult> OnPostAddServerAsync(string? name, string? host, int? port, string? user, string? privateKey, CancellationToken ct)
	{
		host = host?.Trim();
		user = string.IsNullOrWhiteSpace(user) ? "root" : user.Trim();
		if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown || host.StartsWith('-') || !DeploymentEngine.IsSafeHost(host.Replace(":", "", StringComparison.Ordinal)))
		{
			Error = "Enter a name and a valid IP address or hostname.";
			await LoadAsync(ct);
			return Page();
		}
		if (!UserRegex().IsMatch(user))
		{
			Error = "That username isn't valid.";
			await LoadAsync(ct);
			return Page();
		}
		string key;
		string publicKey;
		if (string.IsNullOrWhiteSpace(privateKey))
		{
			var pair = await SshKeys.GenerateAsync($"jfri-{name.Trim()}", ct);
			key = pair.PrivateKey;
			publicKey = pair.PublicKey;
			NewPublicKey = publicKey;
		}
		else
		{
			key = privateKey.Trim();
			if (!key.Contains("PRIVATE KEY", StringComparison.Ordinal))
			{
				Error = "That doesn't look like an SSH private key (OpenSSH or PEM format).";
				await LoadAsync(ct);
				return Page();
			}
			publicKey = "(provided key)";
		}
		db.Servers.Add(new Server
		{
			Name = name.Trim(),
			Provider = "ssh",
			Host = host,
			Port = Math.Clamp(port ?? 22, 1, 65535),
			User = user,
			EncryptedPrivateKey = vault.Protect(key),
			PublicKey = publicKey,
		});
		await db.SaveChangesAsync(ct);
		if (NewPublicKey is null)
		{
			TempData["Message"] = $"Added {name.Trim()}.";
			return Redirect("/connections#servers");
		}
		await LoadAsync(ct);
		return Page();
	}

	public async Task<IActionResult> OnPostDeleteServerAsync(int id, bool destroy, CancellationToken ct)
	{
		var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
		if (server is null)
		{
			return Redirect("/connections#servers");
		}
		if (destroy && server.ProviderServerId is not null && server.ConnectionId is not null)
		{
			var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == server.ConnectionId, ct);
			var provider = providers.FirstOrDefault(p => p.Info.Id == server.Provider);
			if (connection is null || provider is null)
			{
				Error = "Jfri no longer has a token for this server's provider; delete it in the provider's console.";
				await LoadAsync(ct);
				return Page();
			}
			try
			{
				await provider.DeleteAsync(vault.Unprotect(connection.EncryptedToken), server.ProviderServerId, ct);
			}
			catch (Exception ex) when (ex is ProviderApiException or HttpRequestException)
			{
				logger.LogWarning("Destroying server {Server} failed: {Message}", server.Id, ex.Message);
				Error = $"The provider refused: {ex.Message}";
				await LoadAsync(ct);
				return Page();
			}
		}
		db.Servers.Remove(server);
		await db.SaveChangesAsync(ct);
		TempData["Message"] = destroy ? $"Destroyed {server.Name}." : $"Removed {server.Name} from Jfri (the server itself keeps running).";
		return Redirect("/connections#servers");
	}

	async Task LoadAsync(CancellationToken ct)
	{
		Connections = await db.Connections.AsNoTracking().OrderBy(c => c.Provider).ThenBy(c => c.Name).ToListAsync(ct);
		Servers = await db.Servers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
	}

	[GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
	private static partial Regex UserRegex();
}
