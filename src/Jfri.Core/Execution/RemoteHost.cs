using System.Text;

namespace Jfri.Core.Execution;

/// <summary>A server Jfri can run scripts on and copy files to.</summary>
public interface IRemoteHost
{
	string Address { get; }

	/// <summary>Runs a bash script (sent over stdin, never as arguments) and streams its output.</summary>
	Task<int> RunAsync(string script, Action<string> output, CancellationToken ct, TimeSpan? timeout = null);

	/// <summary>Writes a file atomically-enough (umask 077, then chmod) from a stream.</summary>
	Task UploadAsync(string remotePath, Stream content, string mode, CancellationToken ct);
}

/// <summary>Connection details for a server reached over SSH with a key Jfri holds.</summary>
public sealed record SshTarget(string Host, int Port, string User, string PrivateKey, string? KnownHosts);

/// <summary>
/// An <see cref="IRemoteHost"/> backed by the system OpenSSH client. The private key is written
/// to a 0600 file in a 0700 temp directory for the duration of the call and deleted afterwards.
/// The server's host key is pinned: learned on first connect (trust on first use, reported through
/// <see cref="HostKeyLearned"/>) and verified strictly afterwards. Non-root users run under sudo -n.
/// </summary>
public sealed class SshRemoteHost(SshTarget target) : IRemoteHost
{
	public string Address => target.Host;

	/// <summary>Raised with the known_hosts content after the first successful connection.</summary>
	public event Action<string>? HostKeyLearned;

	string? _knownHosts = target.KnownHosts;

	public async Task<int> RunAsync(string script, Action<string> output, CancellationToken ct, TimeSpan? timeout = null)
	{
		var remote = target.User == "root" ? "bash -s" : "sudo -n bash -s";
		using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(WrapScript(script)));
		var result = await Ssh(remote, stdin, output, ct, timeout ?? TimeSpan.FromMinutes(45));
		return result.ExitCode;
	}

	public async Task UploadAsync(string remotePath, Stream content, string mode, CancellationToken ct)
	{
		if (!remotePath.StartsWith('/') || remotePath.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_')) || remotePath.Contains("..", StringComparison.Ordinal))
		{
			throw new ArgumentException($"Refusing to upload to unsafe path {remotePath}.");
		}
		if (mode.Length != 4 || !mode.All(char.IsAsciiDigit))
		{
			throw new ArgumentException("Mode must look like 0644.");
		}
		var dir = remotePath[..remotePath.LastIndexOf('/')];
		var command = $"umask 077 && mkdir -p '{dir}' && cat > '{remotePath}.jfri-tmp' && chmod {mode} '{remotePath}.jfri-tmp' && mv -f '{remotePath}.jfri-tmp' '{remotePath}'";
		var remote = target.User == "root" ? $"sh -c \"{command}\"" : $"sudo -n sh -c \"{command}\"";
		var result = await Ssh(remote, content, null, ct, TimeSpan.FromMinutes(20));
		if (result.ExitCode != 0)
		{
			throw new InvalidOperationException($"Upload of {remotePath} failed: {result.Output.Trim()}");
		}
	}

	/// <summary>
	/// bash -s reads the script from stdin as it runs, so any child that reads stdin (apt, docker
	/// compose run) would swallow the rest of the script. A brace group is parsed in full before it
	/// runs, and its stdin is pointed at /dev/null.
	/// </summary>
	public static string WrapScript(string script) => "{\n" + script.TrimEnd() + "\n} </dev/null\n";

	/// <summary>Polls until SSH answers (fresh servers take a minute to boot).</summary>
	public async Task WaitUntilReachableAsync(Action<string> log, TimeSpan maxWait, CancellationToken ct)
	{
		var deadline = DateTimeOffset.UtcNow + maxWait;
		var attempt = 0;
		while (true)
		{
			attempt++;
			var result = await Ssh("true", null, null, ct, TimeSpan.FromSeconds(30));
			if (result.ExitCode == 0)
			{
				return;
			}
			if (DateTimeOffset.UtcNow > deadline)
			{
				throw new TimeoutException($"SSH to {target.Host} didn't come up: {result.Output.Trim()}");
			}
			if (attempt % 3 == 1)
			{
				log($"Waiting for SSH on {target.Host}...");
			}
			await Task.Delay(TimeSpan.FromSeconds(10), ct);
		}
	}

	async Task<ProcessResult> Ssh(string remoteCommand, Stream? stdin, Action<string>? output, CancellationToken ct, TimeSpan timeout)
	{
		var dir = Directory.CreateTempSubdirectory("jfri-ssh-");
		try
		{
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
			var keyPath = Path.Combine(dir.FullName, "id");
			var knownHostsPath = Path.Combine(dir.FullName, "known_hosts");
			await WritePrivateAsync(keyPath, target.PrivateKey.ReplaceLineEndings("\n").TrimEnd() + "\n", ct);
			var learning = string.IsNullOrWhiteSpace(_knownHosts);
			await WritePrivateAsync(knownHostsPath, _knownHosts ?? "", ct);
			var args = new List<string>
			{
				"-i", keyPath,
				"-p", target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
				"-o", "BatchMode=yes",
				"-o", "IdentitiesOnly=yes",
				"-o", "IdentityAgent=none",
				"-o", $"UserKnownHostsFile={knownHostsPath}",
				"-o", "GlobalKnownHostsFile=/dev/null",
				"-o", $"StrictHostKeyChecking={(learning ? "accept-new" : "yes")}",
				"-o", "HashKnownHosts=no",
				"-o", "ConnectTimeout=15",
				"-o", "ServerAliveInterval=15",
				"-o", "ServerAliveCountMax=8",
				"-o", "LogLevel=ERROR",
				"-l", target.User,
				"--", target.Host,
				remoteCommand,
			};
			var result = await ProcessRunner.RunAsync("ssh", args, ct, environment: new Dictionary<string, string> { ["SSH_AUTH_SOCK"] = "" }, stdin: stdin, onLine: output, timeout: timeout);
			if (learning && result.ExitCode != 255 && File.Exists(knownHostsPath))
			{
				var learned = (await File.ReadAllTextAsync(knownHostsPath, ct)).Trim();
				if (learned.Length > 0)
				{
					_knownHosts = learned;
					HostKeyLearned?.Invoke(learned);
				}
			}
			return result;
		}
		finally
		{
			try
			{
				dir.Delete(recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	static async Task WritePrivateAsync(string path, string content, CancellationToken ct)
	{
		var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}
		await using var stream = new FileStream(path, options);
		await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
	}
}

/// <summary>Generates ed25519 key pairs with ssh-keygen (in a private temp directory).</summary>
public static class SshKeys
{
	public sealed record KeyPair(string PrivateKey, string PublicKey);

	public static async Task<KeyPair> GenerateAsync(string comment, CancellationToken ct)
	{
		var dir = Directory.CreateTempSubdirectory("jfri-keygen-");
		try
		{
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
			var path = Path.Combine(dir.FullName, "id_ed25519");
			var result = await ProcessRunner.RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", path], ct, timeout: TimeSpan.FromSeconds(30));
			if (result.ExitCode != 0)
			{
				throw new InvalidOperationException($"ssh-keygen failed: {result.Output}");
			}
			return new KeyPair(await File.ReadAllTextAsync(path, ct), (await File.ReadAllTextAsync(path + ".pub", ct)).Trim());
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}
}
