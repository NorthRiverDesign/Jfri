using System.Diagnostics;
using System.Text;

namespace Jfri.Core.Execution;

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>
/// Runs a local program with an argument list (never a shell string), optional stdin, a timeout,
/// and line-by-line output streaming. Used for git, ssh and ssh-keygen.
/// </summary>
public static class ProcessRunner
{
	public static async Task<ProcessResult> RunAsync(
		string fileName,
		IEnumerable<string> arguments,
		CancellationToken ct,
		IReadOnlyDictionary<string, string>? environment = null,
		Stream? stdin = null,
		Action<string>? onLine = null,
		TimeSpan? timeout = null,
		string? workingDirectory = null,
		Stream? stdout = null)
	{
		var psi = new ProcessStartInfo(fileName)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
		};
		foreach (var argument in arguments)
		{
			psi.ArgumentList.Add(argument);
		}
		if (environment is not null)
		{
			foreach (var (key, value) in environment)
			{
				psi.Environment[key] = value;
			}
		}
		using var process = new Process { StartInfo = psi };
		var output = new StringBuilder();
		var gate = new object();
		void Line(string? line)
		{
			if (line is null)
			{
				return;
			}
			lock (gate)
			{
				if (output.Length < 256 * 1024)
				{
					output.AppendLine(line);
				}
			}
			onLine?.Invoke(line);
		}
		process.ErrorDataReceived += (_, e) => Line(e.Data);
		if (stdout is null)
		{
			process.OutputDataReceived += (_, e) => Line(e.Data);
		}
		if (!process.Start())
		{
			throw new InvalidOperationException($"Could not start {fileName}.");
		}
		process.BeginErrorReadLine();
		Task copyOut = Task.CompletedTask;
		if (stdout is null)
		{
			process.BeginOutputReadLine();
		}
		else
		{
			copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
		}

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		if (timeout is not null)
		{
			timeoutCts.CancelAfter(timeout.Value);
		}
		try
		{
			if (stdin is not null)
			{
				await stdin.CopyToAsync(process.StandardInput.BaseStream, timeoutCts.Token);
				await process.StandardInput.BaseStream.FlushAsync(timeoutCts.Token);
			}
			process.StandardInput.Close();
			await process.WaitForExitAsync(timeoutCts.Token);
			await copyOut;
		}
		catch (OperationCanceledException)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
			}
			if (ct.IsCancellationRequested)
			{
				throw;
			}
			throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {timeout}.");
		}
		catch (IOException) when (process.HasExited)
		{
			// The remote side closed stdin early (e.g. the script failed); the exit code tells the story.
			await process.WaitForExitAsync(CancellationToken.None);
		}
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, output.ToString());
	}
}
