using System.Globalization;
using System.Text.RegularExpressions;

namespace Nullean.Snapshots;

/// <summary>
/// Built-in scrubbers. Each replaces volatile values with numbered placeholders, the same value getting the same
/// number within one snapshot, so references between values survive. None is applied unless you add it, per call
/// through <see cref="SnapshotOptions.Scrub(Func{string, string})"/> or for every snapshot through
/// <see cref="SnapshotDefaults.AddScrubber(Func{string, string})"/>.
/// </summary>
public static partial class Scrubbers
{
	/// <summary>Replaces GUIDs (with or without braces, any case) with <c>Guid_1</c>, <c>Guid_2</c>, ….</summary>
	public static string Guids(string text) => Number(text, GuidPattern(), "Guid_");

	/// <summary>
	/// Replaces ISO 8601 date-times (<c>2026-11-03T08:53:10</c>, with optional fractions and offset or <c>Z</c>) with
	/// <c>DateTime_1</c>, <c>DateTime_2</c>, ….
	/// </summary>
	public static string DateTimes(string text) => Number(text, DateTimePattern(), "DateTime_");

	/// <summary>A scrubber that replaces every match of <paramref name="pattern"/> with <paramref name="replacement"/>.</summary>
	public static Func<string, string> Regex(Regex pattern, string replacement)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(replacement);
		return text => pattern.Replace(text, replacement);
	}

	private static string Number(string text, Regex pattern, string prefix)
	{
		ArgumentNullException.ThrowIfNull(text);
		var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		return pattern.Replace(text, match =>
		{
			if (!seen.TryGetValue(match.Value, out var n))
				seen[match.Value] = n = seen.Count + 1;
			return prefix + n.ToString(CultureInfo.InvariantCulture);
		});
	}

	[GeneratedRegex(@"(?<![0-9A-Za-z_])\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?(?![0-9A-Za-z_])")]
	private static partial Regex GuidPattern();

	[GeneratedRegex(@"(?<![0-9A-Za-z_])\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,9})?)?(Z|[+-]\d{2}:?\d{2})?(?![0-9A-Za-z_])")]
	private static partial Regex DateTimePattern();
}
