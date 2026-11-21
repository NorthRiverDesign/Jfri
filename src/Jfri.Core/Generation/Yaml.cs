using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jfri.Core.Generation;

internal static class Yaml
{
	static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	/// <summary>Quotes a scalar as a JSON string, which is always a valid YAML double-quoted scalar.</summary>
	public static string Q(string value) => JsonSerializer.Serialize(value, Options);

	/// <summary>A flow sequence of quoted strings: ["a", "b"].</summary>
	public static string List(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Q)) + "]";

	/// <summary>Splits a shell command into an exec-form list run through sh -c (keeps quoting and env expansion intact).</summary>
	public static string Shell(string command) => List(["sh", "-c", command]);
}
