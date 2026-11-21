using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jfri.Core.Analysis;

/// <summary>Small, tolerant parsers for the manifest formats detectors read.</summary>
internal static partial class Parsing
{
	static readonly string[] SecretHints = ["KEY", "SECRET", "PASSWORD", "PASS", "TOKEN", "SALT", "PRIVATE", "CREDENTIAL", "DSN"];

	/// <summary>Parses a dotenv file (KEY=value lines, # comments, optional quotes and "export").</summary>
	public static List<KeyValuePair<string, string>> ParseEnv(string? text)
	{
		var result = new List<KeyValuePair<string, string>>();
		if (text is null)
		{
			return result;
		}
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#'))
			{
				continue;
			}
			if (line.StartsWith("export ", StringComparison.Ordinal))
			{
				line = line[7..].TrimStart();
			}
			var eq = line.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			var key = line[..eq].Trim();
			if (!EnvKeyRegex().IsMatch(key))
			{
				continue;
			}
			var value = line[(eq + 1)..].Trim();
			if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
			{
				value = value[1..^1];
			}
			else
			{
				var hash = value.IndexOf(" #", StringComparison.Ordinal);
				if (hash >= 0)
				{
					value = value[..hash].TrimEnd();
				}
			}
			result.Add(new(key, value));
		}
		return result;
	}

	public static bool LooksSecret(string key) => SecretHints.Any(h => key.Contains(h, StringComparison.OrdinalIgnoreCase));

	public static string? EnvValue(List<KeyValuePair<string, string>> env, string key) =>
		env.FirstOrDefault(kv => kv.Key == key).Value is { Length: > 0 } v ? v : null;

	public static JsonDocument? Json(string? text)
	{
		if (text is null)
		{
			return null;
		}
		try
		{
			return JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static XDocument? Xml(string? text)
	{
		if (text is null)
		{
			return null;
		}
		try
		{
			// XDocument.Parse never resolves external entities or DTDs, so repo XML can't reach the filesystem or network.
			return XDocument.Parse(text);
		}
		catch (System.Xml.XmlException)
		{
			return null;
		}
	}

	public static string? GetString(this JsonElement element, params string[] path)
	{
		var current = element;
		foreach (var segment in path)
		{
			if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
			{
				return null;
			}
		}
		return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
	}

	public static JsonElement? GetObject(this JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

	/// <summary>Names of the keys of the given JSON object properties (e.g. dependencies + devDependencies).</summary>
	public static HashSet<string> KeysOf(JsonElement root, params string[] objectNames)
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var name in objectNames)
		{
			if (root.GetObject(name) is { } obj)
			{
				foreach (var p in obj.EnumerateObject())
				{
					set.Add(p.Name);
				}
			}
		}
		return set;
	}

	/// <summary>Very small TOML reader: returns key = "value" pairs inside a given [table] (no nesting).</summary>
	public static string? TomlValue(string? text, string table, string key)
	{
		if (text is null)
		{
			return null;
		}
		var inTable = table.Length == 0;
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.Trim();
			if (line.StartsWith('['))
			{
				inTable = line.Trim('[', ']', ' ') == table;
				continue;
			}
			if (!inTable)
			{
				continue;
			}
			var m = TomlPairRegex().Match(line);
			if (m.Success && m.Groups[1].Value == key)
			{
				return m.Groups[2].Value;
			}
		}
		return null;
	}

	/// <summary>Returns the image references (e.g. "postgres:16-alpine") used in a compose file.</summary>
	public static IEnumerable<string> ComposeImages(string? text) =>
		text is null ? [] : ComposeImageRegex().Matches(text).Select(m => m.Groups[1].Value.Trim('"', '\''));

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*$")]
	private static partial Regex EnvKeyRegex();

	[GeneratedRegex(@"^([A-Za-z0-9_\-]+)\s*=\s*[""']([^""']*)[""']")]
	private static partial Regex TomlPairRegex();

	[GeneratedRegex(@"^\s*image:\s*(\S+)", RegexOptions.Multiline)]
	private static partial Regex ComposeImageRegex();
}
