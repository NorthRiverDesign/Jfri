using System.Globalization;
using System.Text.RegularExpressions;

namespace Jfri.Core.Analysis;

/// <summary>
/// A forgiving parser for the version constraints package managers use (composer, npm engines,
/// requires-python). It only needs to answer "is version X allowed?" so Jfri can pick the
/// newest well-supported runtime that the repo accepts.
/// </summary>
public static partial class VersionRange
{
	/// <summary>
	/// Picks a runtime version for a constraint. Returns <paramref name="preferred"/> if allowed,
	/// otherwise the highest allowed candidate below it, otherwise the lowest allowed above it.
	/// Returns null when the constraint is empty or unparseable (callers fall back to the default).
	/// </summary>
	public static string? Pick(string? constraint, string preferred, IEnumerable<string> candidates)
	{
		if (string.IsNullOrWhiteSpace(constraint))
		{
			return null;
		}
		var alternatives = ParseAlternatives(constraint);
		if (alternatives.Count == 0)
		{
			return null;
		}
		bool Allowed(string v) => alternatives.Any(set => set.All(c => c(ParseVersion(v))));
		if (Allowed(preferred))
		{
			return preferred;
		}
		var ordered = candidates.Distinct().OrderBy(ParseVersion).ToList();
		var pref = ParseVersion(preferred);
		var below = ordered.Where(v => ParseVersion(v) < pref && Allowed(v)).LastOrDefault();
		return below ?? ordered.FirstOrDefault(v => ParseVersion(v) > pref && Allowed(v));
	}

	/// <summary>Extracts the leading "major" or "major.minor" from a version-ish string, e.g. "v20.11.1" → "20".</summary>
	public static string? Major(string? text)
	{
		if (text is null)
		{
			return null;
		}
		var m = NumberRegex().Match(text);
		return m.Success ? m.Groups[1].Value : null;
	}

	/// <summary>Extracts "major.minor" from a version-ish string, e.g. "3.12.1" → "3.12".</summary>
	public static string? MajorMinor(string? text)
	{
		if (text is null)
		{
			return null;
		}
		var m = MajorMinorRegex().Match(text);
		return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : null;
	}

	internal static Version ParseVersion(string text)
	{
		var parts = text.Trim().TrimStart('v', 'V', '=').Split('.', StringSplitOptions.RemoveEmptyEntries)
			.Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
			.Concat([0, 0, 0])
			.Take(3)
			.ToArray();
		return new Version(parts[0], parts[1], parts[2]);
	}

	static List<List<Func<Version, bool>>> ParseAlternatives(string constraint)
	{
		var result = new List<List<Func<Version, bool>>>();
		foreach (var alt in constraint.Split(["||", "|"], StringSplitOptions.RemoveEmptyEntries))
		{
			var set = new List<Func<Version, bool>>();
			var normalized = OperatorSpaceRegex().Replace(alt.Trim(), "$1");
			// "1.2 - 2.3" hyphen ranges
			var hyphen = HyphenRegex().Match(normalized);
			if (hyphen.Success)
			{
				var lo = ParseVersion(hyphen.Groups[1].Value);
				var hi = ParseVersion(hyphen.Groups[2].Value);
				set.Add(v => v >= lo && v <= hi);
				result.Add(set);
				continue;
			}
			foreach (var token in normalized.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
			{
				var c = ParseToken(token);
				if (c is not null)
				{
					set.Add(c);
				}
			}
			if (set.Count > 0)
			{
				result.Add(set);
			}
		}
		return result;
	}

	static Func<Version, bool>? ParseToken(string token)
	{
		token = token.Trim();
		if (token is "*" or "x" or "latest")
		{
			return _ => true;
		}
		var m = TokenRegex().Match(token);
		if (!m.Success)
		{
			return null;
		}
		var op = m.Groups[1].Value;
		var raw = m.Groups[2].Value;
		var segments = raw.Split('.').Where(s => s.Length > 0 && s is not "*" and not "x" and not "X").ToArray();
		if (segments.Length == 0)
		{
			return _ => true;
		}
		var v = ParseVersion(string.Join('.', segments));
		var wildcard = raw.Contains('*') || raw.Contains('x', StringComparison.OrdinalIgnoreCase);
		switch (op)
		{
			case "^":
				// ^8.2 → >=8.2 <9 ; ^0.3 → >=0.3 <0.4
				var caretUpper = v.Major > 0 ? new Version(v.Major + 1, 0, 0) : new Version(0, v.Minor + 1, 0);
				return x => x >= v && x < caretUpper;
			case "~":
				// ~8.2 → >=8.2 <9 (composer) ; ~8.2.1 → >=8.2.1 <8.3
				var tildeUpper = segments.Length >= 3 ? new Version(v.Major, v.Minor + 1, 0) : new Version(v.Major + 1, 0, 0);
				return x => x >= v && x < tildeUpper;
			case "~=":
				// PEP 440: ~=3.11 → >=3.11 <4 ; ~=3.11.2 → >=3.11.2 <3.12
				var compatUpper = segments.Length >= 3 ? new Version(v.Major, v.Minor + 1, 0) : new Version(v.Major + 1, 0, 0);
				return x => x >= v && x < compatUpper;
			case ">=":
				return x => x >= v;
			case ">":
				return segments.Length == 1 ? x => x.Major > v.Major : x => x > v;
			case "<=":
				return segments.Length == 1 ? x => x.Major <= v.Major : x => x <= v;
			case "<":
				return x => x < v;
			case "!=":
				return x => !SamePrefix(x, v, segments.Length);
			default:
				// "8.2", "==3.12", "20.x", "8.2.*": match the given prefix
				return x => SamePrefix(x, v, wildcard ? segments.Length : Math.Min(segments.Length, 2));
		}
	}

	static bool SamePrefix(Version x, Version v, int segments) => segments switch
	{
		1 => x.Major == v.Major,
		2 => x.Major == v.Major && x.Minor == v.Minor,
		_ => x.Major == v.Major && x.Minor == v.Minor && x.Build == v.Build,
	};

	[GeneratedRegex(@"(\d+)")]
	private static partial Regex NumberRegex();

	[GeneratedRegex(@"(\d+)\.(\d+)")]
	private static partial Regex MajorMinorRegex();

	[GeneratedRegex(@"(>=|<=|~=|==|!=|>|<|\^|~|=)\s+")]
	private static partial Regex OperatorSpaceRegex();

	[GeneratedRegex(@"^\s*v?([\d.]+)\s+-\s+v?([\d.]+)\s*$")]
	private static partial Regex HyphenRegex();

	[GeneratedRegex(@"^(>=|<=|~=|==|!=|>|<|\^|~|=)?v?([\d.*xX]+)")]
	private static partial Regex TokenRegex();
}
