using System.Net;
using System.Text;
using Jfri.Core.Analysis;
using Jfri.Core.Execution;
using Jfri.Core.Planning;

namespace Jfri.Core.Tests;

internal static class Fixtures
{
	public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

	public static RepositoryProfile Analyze(string name) => RepositoryAnalyzer.Analyze(Path(name), name);

	public static DeploymentPlan Plan(string name, RuntimeKind runtime = RuntimeKind.Kubernetes, string? domain = null) =>
		Planner.Plan(Analyze(name), new PlanOptions { Runtime = runtime, Domain = domain }, name);
}

/// <summary>Records every request and answers from a routing function.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, string?, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
{
	public List<(HttpMethod Method, string Url, string? Body, string? Authorization)> Requests { get; } = [];

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		Requests.Add((request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
		var (status, responseBody) = respond(request, body);
		return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
	}
}

/// <summary>An in-memory server: records scripts run and files uploaded.</summary>
internal sealed class FakeRemoteHost : IRemoteHost
{
	public string Address => "203.0.113.7";
	public List<string> Scripts { get; } = [];
	public Dictionary<string, (string Content, string Mode)> Files { get; } = [];
	public List<string> Events { get; } = [];
	/// <summary>Exit code to return for scripts containing the given marker.</summary>
	public Dictionary<string, int> FailWhen { get; } = [];

	public Task<int> RunAsync(string script, Action<string> output, CancellationToken ct, TimeSpan? timeout = null)
	{
		Scripts.Add(script);
		Events.Add("run:" + script.Split('\n').FirstOrDefault(l => l.StartsWith("echo '==>", StringComparison.Ordinal) || l.StartsWith("rm -rf", StringComparison.Ordinal) || l.StartsWith("for i", StringComparison.Ordinal)));
		output("remote says hello");
		foreach (var (marker, code) in FailWhen)
		{
			if (script.Contains(marker, StringComparison.Ordinal))
			{
				output("something broke");
				return Task.FromResult(code);
			}
		}
		return Task.FromResult(0);
	}

	public async Task UploadAsync(string remotePath, Stream content, string mode, CancellationToken ct)
	{
		using var reader = new MemoryStream();
		await content.CopyToAsync(reader, ct);
		Files[remotePath] = (Encoding.UTF8.GetString(reader.ToArray()), mode);
		Events.Add("upload:" + remotePath);
	}
}
