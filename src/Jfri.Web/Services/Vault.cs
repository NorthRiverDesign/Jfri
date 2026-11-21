using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Jfri.Web.Services;

/// <summary>
/// Encrypts secrets at rest (provider tokens, SSH keys, generated app secrets) with ASP.NET Core
/// Data Protection. The key ring lives in the data directory, outside the database, so a leaked
/// database alone reveals nothing.
/// </summary>
public sealed class Vault(IDataProtectionProvider provider)
{
	readonly IDataProtector _protector = provider.CreateProtector("Jfri.Vault.v1");

	public string Protect(string plaintext) => _protector.Protect(plaintext);

	public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);

	public string ProtectMap(IReadOnlyDictionary<string, string> values) => Protect(JsonSerializer.Serialize(values));

	public Dictionary<string, string> UnprotectMap(string? ciphertext) =>
		ciphertext is null ? new(StringComparer.Ordinal) : JsonSerializer.Deserialize<Dictionary<string, string>>(Unprotect(ciphertext)) ?? new(StringComparer.Ordinal);

	public static string Hint(string token) => token.Length <= 8 ? "••••" : "••••" + token[^4..];

	public static string NewWebhookSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

/// <summary>
/// First-run protection: until an admin account exists, creating one requires a one-time token
/// printed to the server log and written (0600) to the data directory, so nobody who merely
/// reaches the web page first can claim the instance.
/// </summary>
public sealed class SetupToken
{
	readonly string _path;
	string? _token;

	public SetupToken(JfriPaths paths)
	{
		_path = Path.Combine(paths.DataDir, "setup-token");
	}

	public string FilePath => _path;

	public string Ensure()
	{
		if (_token is not null)
		{
			return _token;
		}
		if (File.Exists(_path))
		{
			_token = File.ReadAllText(_path).Trim();
			if (_token.Length >= 16)
			{
				return _token;
			}
		}
		_token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
		var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}
		using (var stream = new FileStream(_path, options))
		using (var writer = new StreamWriter(stream))
		{
			writer.Write(_token);
		}
		return _token;
	}

	public bool Matches(string? candidate)
	{
		var expected = Ensure();
		return candidate is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(candidate.Trim()), System.Text.Encoding.UTF8.GetBytes(expected));
	}

	public void Consume()
	{
		_token = null;
		if (File.Exists(_path))
		{
			File.Delete(_path);
		}
	}
}

public sealed class JfriPaths
{
	public JfriPaths(string dataDir)
	{
		DataDir = Path.GetFullPath(dataDir);
		Directory.CreateDirectory(DataDir);
		Directory.CreateDirectory(Keys);
		Directory.CreateDirectory(Workspaces);
		Directory.CreateDirectory(Logs);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	public string DataDir { get; }
	public string Keys => Path.Combine(DataDir, "keys");
	public string Workspaces => Path.Combine(DataDir, "workspaces");
	public string Logs => Path.Combine(DataDir, "logs");
	public string Database => Path.Combine(DataDir, "jfri.db");

	public string Workspace(int appId) => Path.Combine(Workspaces, $"app-{appId}");

	public string DeploymentLog(int deploymentId) => Path.Combine(Logs, $"deployment-{deploymentId}.log");

	public static string Default() =>
		Environment.GetEnvironmentVariable("JFRI_DATA_DIR") is { Length: > 0 } configured
			? configured
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "jfri");
}
