using System.Text;
using Jfri.Core.Execution;

namespace Jfri.Core.Sources;

/// <summary>
/// Clones repositories safely. Only https URLs are accepted, credentials never appear in the URL
/// or argument list (they're passed as an HTTP header through git's environment config), clones
/// are shallow, submodules, hooks and symlinks are disabled, and size is capped.
/// </summary>
public static class GitRepository
{
	public const long MaxCheckoutBytes = 750L * 1024 * 1024;
	static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(5);

	/// <summary>Returns an error message, or null when the URL is acceptable.</summary>
	public static string? ValidateUrl(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return "Enter a repository URL.";
		}
		url = url.Trim();
		if (url.StartsWith('-'))
		{
			return "That isn't a valid repository URL.";
		}
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
		{
			return "Use an https:// repository URL (for example https://github.com/owner/repo).";
		}
		if (!string.IsNullOrEmpty(uri.UserInfo))
		{
			return "Don't put credentials in the URL; use the access token field instead.";
		}
		if (url.Any(char.IsWhiteSpace) || url.Any(char.IsControl))
		{
			return "That isn't a valid repository URL.";
		}
		return null;
	}

	public static bool IsValidBranch(string? branch) =>
		string.IsNullOrEmpty(branch) || (!branch.StartsWith('-') && !branch.Contains("..", StringComparison.Ordinal) && branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.') && branch.Length <= 200);

	/// <summary>Name for display, derived from the URL ("https://github.com/acme/shop.git" → "shop").</summary>
	public static string NameFromUrl(string url)
	{
		var path = new Uri(url).AbsolutePath.TrimEnd('/');
		var name = path[(path.LastIndexOf('/') + 1)..];
		return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
	}

	/// <summary>Shallow-clones <paramref name="url"/> into <paramref name="destination"/> (replacing it).</summary>
	public static async Task CloneAsync(string url, string? branch, string? token, string destination, Action<string>? log, CancellationToken ct)
	{
		if (ValidateUrl(url) is { } error)
		{
			throw new ArgumentException(error, nameof(url));
		}
		if (!IsValidBranch(branch))
		{
			throw new ArgumentException("Invalid branch name.", nameof(branch));
		}
		if (Directory.Exists(destination))
		{
			Directory.Delete(destination, recursive: true);
		}
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		var args = new List<string>
		{
			"-c", "core.symlinks=false",
			"-c", "core.hooksPath=/dev/null",
			"-c", "protocol.allow=never",
			"-c", "protocol.https.allow=always",
			"-c", "transfer.fsckObjects=true",
			"clone", "--depth", "1", "--no-recurse-submodules", "--single-branch",
		};
		if (!string.IsNullOrEmpty(branch))
		{
			args.Add("--branch");
			args.Add(branch);
		}
		args.Add("--");
		args.Add(url.Trim());
		args.Add(destination);
		var result = await ProcessRunner.RunAsync("git", args, ct, Environment(token), onLine: line => log?.Invoke(Redact(line, token)), timeout: CloneTimeout);
		if (result.ExitCode != 0)
		{
			TryDelete(destination);
			var message = Redact(result.Output, token).Trim();
			throw new InvalidOperationException(message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) || message.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
				? "The repository needs an access token (or the token doesn't have read access)."
				: $"git clone failed: {Last(message, 400)}");
		}
		var size = DirectorySize(destination);
		if (size > MaxCheckoutBytes)
		{
			TryDelete(destination);
			throw new InvalidOperationException($"The repository checkout is {size / 1024 / 1024} MB; Jfri's limit is {MaxCheckoutBytes / 1024 / 1024} MB.");
		}
	}

	public static async Task<string> HeadAsync(string directory, CancellationToken ct)
	{
		var result = await ProcessRunner.RunAsync("git", ["-C", directory, "rev-parse", "HEAD"], ct, Environment(null), timeout: TimeSpan.FromSeconds(30));
		return result.ExitCode == 0 ? result.Output.Trim() : throw new InvalidOperationException("Not a git checkout.");
	}

	/// <summary>Writes a gzipped tarball of HEAD (exactly the committed files, nothing from the working tree).</summary>
	public static async Task ArchiveAsync(string directory, Stream output, CancellationToken ct)
	{
		var result = await ProcessRunner.RunAsync("git", ["-C", directory, "archive", "--format=tar.gz", "HEAD"], ct, Environment(null), timeout: TimeSpan.FromMinutes(5), stdout: output);
		if (result.ExitCode != 0)
		{
			throw new InvalidOperationException($"git archive failed: {Last(result.Output, 300)}");
		}
	}

	static Dictionary<string, string> Environment(string? token)
	{
		var env = new Dictionary<string, string>
		{
			["GIT_TERMINAL_PROMPT"] = "0",
			["GIT_ASKPASS"] = "true",
			["GIT_CONFIG_NOSYSTEM"] = "1",
			["GIT_LFS_SKIP_SMUDGE"] = "1",
			["GIT_CONFIG_COUNT"] = "0",
		};
		if (!string.IsNullOrEmpty(token))
		{
			var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
			env["GIT_CONFIG_COUNT"] = "1";
			env["GIT_CONFIG_KEY_0"] = "http.extraHeader";
			env["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}";
		}
		return env;
	}

	static string Redact(string text, string? token) => string.IsNullOrEmpty(token) ? text : text.Replace(token, "***", StringComparison.Ordinal);

	static string Last(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

	static long DirectorySize(string dir) =>
		new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);

	static void TryDelete(string dir)
	{
		try
		{
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, recursive: true);
			}
		}
		catch (IOException)
		{
		}
	}
}
