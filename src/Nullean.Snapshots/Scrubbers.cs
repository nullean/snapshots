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

	/// <summary>
	/// A scrubber that replaces the folder <paramref name="root"/> with <paramref name="replacement"/> wherever it starts a path, and writes the rest of that path with <c>/</c>, so a run on Windows
	/// (<c>C:\work\repo\docs\a.md</c>, or its JSON-escaped <c>C:\\work\\repo</c>) makes the same text as one elsewhere (<c>{repo}/docs/a.md</c>). <paramref name="root"/> may be written with either separator and
	/// a trailing one. It matches only a whole folder name (<c>/tmp</c> does not touch <c>/tmpfiles</c> or <c>/var/tmp</c>) and ignores case on Windows only.
	/// </summary>
	/// <exception cref="ArgumentException"><paramref name="root"/> is empty or only separators.</exception>
	public static Func<string, string> Path(string root, string replacement)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(replacement);
		var normalised = root.Replace('\\', '/').TrimEnd('/');
		if (normalised.Length == 0)
			throw new ArgumentException("The root must be a folder, not empty or the file system root.", nameof(root));
		const string separator = @"(?:\\{1,2}|/)";
		var pattern = new System.Text.RegularExpressions.Regex(
			@"(?<![\w.\-])" + string.Join(separator, normalised.Split('/').Select(System.Text.RegularExpressions.Regex.Escape)) + @"(?:(?<tail>" + separator +
				@"[^\s""'<>|*?:]*)|(?![\w.\-]))",
			System.Text.RegularExpressions.RegexOptions.CultureInvariant |
				(OperatingSystem.IsWindows() ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None),
			TimeSpan.FromSeconds(5)
		);
		return text =>
		{
			ArgumentNullException.ThrowIfNull(text);
			return pattern.Replace(text, m => replacement + BackslashRuns().Replace(m.Groups["tail"].Value, "/"));
		};
	}

	/// <summary>
	/// <see cref="Path"/> for the system's temp folder, <c>{temp}</c> by default. Where the temp folder is a link (<c>/var</c> is <c>/private/var</c> on macOS) the resolved path is scrubbed too, as a
	/// process may print either.
	/// </summary>
	public static Func<string, string> TempDirectory(string replacement = "{temp}")
	{
		var temp = System.IO.Path.GetTempPath();
		var scrubbers = new List<Func<string, string>>();
		var resolved = new DirectoryInfo(temp).ResolveLinkTarget(true)?.FullName;
		foreach (var root in new[] { resolved, temp, resolved is null && temp.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + temp : null }
			.Where(r => !string.IsNullOrEmpty(r))
			.Distinct(StringComparer.Ordinal)
			.OrderByDescending(r => r!.Length))
			scrubbers.Add(Path(root!, replacement));
		return text => scrubbers.Aggregate(text, (t, scrub) => scrub(t));
	}

	[GeneratedRegex(@"\\+")]
	private static partial Regex BackslashRuns();

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
