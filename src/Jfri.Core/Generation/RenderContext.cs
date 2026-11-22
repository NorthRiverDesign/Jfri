using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jfri.Core.Planning;

namespace Jfri.Core.Generation;

/// <summary>
/// Values only known at deploy time. When <see cref="Secrets"/> is null the artifacts are rendered
/// for preview/download: secret values become placeholders and are never written out.
/// </summary>
public sealed record RenderContext(string Host, string Revision, IReadOnlyDictionary<string, string>? Secrets)
{
	public const string PreviewHost = "app.example.com";

	public static RenderContext Preview(DeploymentPlan plan) => new(plan.Domain ?? PreviewHost, "latest", null);

	public bool IsPreview => Secrets is null;
	public string Url => $"https://{Host}";
}

/// <summary>An output file: a path relative to the artifact root, its content and its Unix mode.</summary>
public sealed record Artifact(string Path, string Content, string Mode = "0644", bool Sensitive = false);

public static partial class Template
{
	/// <summary>Replaces {{secret:NAME}}, {{url}} and {{host}} tokens.</summary>
	public static string Resolve(string value, RenderContext context) => TokenRegex().Replace(value, m =>
	{
		var token = m.Groups[1].Value;
		if (token == "url")
		{
			return context.Url;
		}
		if (token == "host")
		{
			return context.Host;
		}
		var name = token["secret:".Length..];
		if (context.Secrets is null)
		{
			return $"<{name}>";
		}
		return context.Secrets.TryGetValue(name, out var secret) ? secret : throw new InvalidOperationException($"Secret {name} has no value.");
	});

	[GeneratedRegex(@"\{\{(url|host|secret:[A-Z0-9_]+)\}\}")]
	private static partial Regex TokenRegex();
}

/// <summary>Generates secret values with a CSPRNG. Existing values are always kept, so redeploys never rotate a database password out from under its data.</summary>
public static class SecretGenerator
{
	const string Alphanumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

	public static Dictionary<string, string> Fill(IEnumerable<PlannedSecret> secrets, IReadOnlyDictionary<string, string>? existing)
	{
		var result = existing is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(existing, StringComparer.Ordinal);
		foreach (var secret in secrets)
		{
			if (!result.ContainsKey(secret.Name) && secret.Kind != SecretKind.UserProvided)
			{
				result[secret.Name] = Generate(secret.Kind);
			}
		}
		return result;
	}

	/// <summary>
	/// The secret values a deploy renders with: generated values (missing ones filled in) with the
	/// user's settings on top. Returns the generated map separately so the caller can store it without
	/// the user's values mixed in. Throws when a planned setting has no value.
	/// </summary>
	public static (Dictionary<string, string> Generated, Dictionary<string, string> All) ForDeploy(
		IReadOnlyCollection<PlannedSecret> secrets, IReadOnlyDictionary<string, string>? generated, IReadOnlyDictionary<string, string>? user)
	{
		var filled = Fill(secrets, generated);
		var all = new Dictionary<string, string>(filled, StringComparer.Ordinal);
		foreach (var (name, value) in user ?? new Dictionary<string, string>())
		{
			if (!Planner.ManagedSecrets.Contains(name))
			{
				all[name] = value;
			}
		}
		var missing = secrets.Where(s => !all.ContainsKey(s.Name)).Select(s => s.Name).ToList();
		if (missing.Count > 0)
		{
			throw new InvalidOperationException($"Set a value for {string.Join(", ", missing)} on the app's page.");
		}
		return (filled, all);
	}

	public static string Generate(SecretKind kind) => kind switch
	{
		SecretKind.LaravelKey => "base64:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
		SecretKind.Hex64 => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(64)),
		SecretKind.Long => RandomNumberGenerator.GetString(Alphanumeric, 50),
		_ => RandomNumberGenerator.GetString(Alphanumeric, 32),
	};
}
