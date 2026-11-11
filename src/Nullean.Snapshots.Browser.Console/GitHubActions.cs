using System.Globalization;
using System.Text;

namespace Nullean.Snapshots.Browser.Console;

/// <summary>What a run shows GitHub Actions: annotations on the journey that failed, log groups, and the step summary.</summary>
internal static class GitHubActions
{
	/// <summary>GitHub shows at most ten error annotations per step; past that a run adds one warning that says how many are not shown.</summary>
	internal const int MaxAnnotations = 10;

	/// <summary>More failures than this are folded into log groups, so a run with many does not bury the totals.</summary>
	internal const int ExpandedFailures = 3;

	/// <summary>A run of at most this many journeys lists every one in the summary table; a longer one lists the failures.</summary>
	internal const int ListedJourneys = 10;

	/// <summary>True when the process runs inside a GitHub Actions job.</summary>
	public static bool Detected => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

	/// <summary>The data of a workflow command: <c>%</c>, carriage return and newline are escaped.</summary>
	internal static string Escape(string data) =>
		data.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

	/// <summary>A property of a workflow command: as data, and <c>:</c> and <c>,</c> too.</summary>
	internal static string EscapeProperty(string value) =>
		Escape(value).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);

	/// <summary>The path an annotation attaches to: relative to the workspace, or <c>null</c> for a file outside it.</summary>
	internal static string? AnnotationFile(JourneyRunFile file)
	{
		var path = file.Display ?? file.Path;
		if (!Path.IsPathRooted(path))
			return path.Replace('\\', '/');
		if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is not { Length: > 0 } workspace)
			return null;
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar;
		var full = Path.GetFullPath(path);
		return full.StartsWith(root, StringComparison.Ordinal) ? Path.GetRelativePath(root, full).Replace('\\', '/') : null;
	}

	/// <summary>One error annotation per failed journey, at the failing step when there is one, and a warning for those past the limit. Skipped journeys have none.</summary>
	internal static IReadOnlyList<string> Annotations(IReadOnlyList<JourneyOutcome> outcomes)
	{
		var failed = outcomes.Where(o => o.Failed).ToList();
		var lines = failed.Take(MaxAnnotations).Select(Annotation).ToList();
		if (failed.Count > MaxAnnotations)
			lines.Add($"::warning title=Journeys::{Escape($"{failed.Count - MaxAnnotations} more failed journeys have no annotation: see the step summary.")}");
		return lines;
	}

	private static string Annotation(JourneyOutcome outcome)
	{
		var failure = outcome.Transcript?.Failure;
		var properties = new List<string>();
		if (AnnotationFile(outcome.File) is { } file)
		{
			properties.Add($"file={EscapeProperty(file)}");
			if (failure is not null)
				properties.Add($"line={failure.Line.ToString(CultureInfo.InvariantCulture)}");
		}
		properties.Add($"title={EscapeProperty($"Journey failed: {outcome.File.Id}")}");
		var message = outcome.Error ?? (failure is null ? outcome.Compared?.FailureMessage ?? "The transcript differs." : $"{failure.Command}\n{failure.Message}");
		if (failure?.Url is { Length: > 0 } url)
			message += $"\nPage: {url}";
		return $"::error {string.Join(',', properties)}::{Escape(message)}";
	}

	/// <summary>Opens a log group. A title is one line.</summary>
	internal static string Group(string title) => $"::group::{title.ReplaceLineEndings(" ")}";

	internal const string EndGroup = "::endgroup::";

	/// <summary>
	/// The summary: one line of totals, one line for what was skipped, the journeys that ran (every one when there are few, the
	/// failures when there are many), and each failure with its step, error, page and rerun command.
	/// </summary>
	internal static string Summary(Uri? site, IReadOnlyList<JourneyOutcome> outcomes, string? rerun)
	{
		var passed = outcomes.Count(o => o.Passed);
		var failed = outcomes.Where(o => o.Failed).ToList();
		var skipped = outcomes.Where(o => o.Skipped).ToList();
		var text = new StringBuilder();
		_ = text.AppendLine(site is null ? "### Journeys" : $"### Journeys against {site}").AppendLine();
		_ = text.AppendLine(CultureInfo.InvariantCulture, $"✅ **{passed} passed** · {(failed.Count > 0 ? $"❌ **{failed.Count} failed**" : "0 failed")} · ⊘ {skipped.Count} skipped");
		if (skipped.Count > 0)
			_ = text.AppendLine().AppendLine(SkippedLine(skipped));

		var ran = outcomes.Where(o => !o.Skipped).ToList();
		var listed = ran.Count <= ListedJourneys ? ran : failed;
		if (listed.Count > 0)
		{
			_ = text.AppendLine().AppendLine("| Journey | Result | Time |").AppendLine("|---|---|---|");
			foreach (var outcome in listed)
				_ = text.AppendLine(
					CultureInfo.InvariantCulture,
					$"| `{outcome.File.Id}` | {(outcome.Passed ? "passed" : "**failed**")} | {outcome.Elapsed.TotalSeconds:0.0}s |"
				);
		}

		foreach (var outcome in failed)
			_ = text.AppendLine().Append(FailureSection(outcome, rerun?.Replace("{id}", outcome.File.Id, StringComparison.Ordinal)));
		return text.ToString();
	}

	/// <summary>Skipped journeys on one line: each by name when there are few, otherwise counted per reason.</summary>
	private static string SkippedLine(List<JourneyOutcome> skipped) =>
		skipped.Count <= 3
			? "⊘ Skipped " + string.Join(", ", skipped.Select(o => $"`{o.File.Id}` ({o.SkipReason})"))
			: $"⊘ Skipped {skipped.Count}: " + string.Join(", ", skipped.GroupBy(o => o.SkipReason).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})"));

	private static string FailureSection(JourneyOutcome outcome, string? rerun)
	{
		var failure = outcome.Transcript?.Failure;
		var text = new StringBuilder();
		_ = text.AppendLine(CultureInfo.InvariantCulture, $"#### ❌ {outcome.File.Id}").AppendLine();
		if (failure is not null)
			_ = text.AppendLine(CultureInfo.InvariantCulture, $"`{outcome.File.Display ?? outcome.File.Path}:{failure.Line}`: `{failure.Command}`").AppendLine();
		var message = outcome.Error ?? failure?.Message ?? outcome.Compared?.FailureMessage ?? "The transcript differs.";
		_ = text.AppendLine("```text").AppendLine(message.Replace("```", "'''", StringComparison.Ordinal).TrimEnd()).AppendLine("```");
		var facts = new List<string>();
		if (failure?.Url is { Length: > 0 } url)
			facts.Add($"Page: {url}");
		if (failure?.ScreenshotPath is { } screenshot)
			facts.Add($"Screenshot: `{screenshot}`");
		if (rerun is not null)
			facts.Add($"Rerun: `{rerun}`");
		if (facts.Count > 0)
			_ = text.AppendLine().AppendLine(string.Join(" · ", facts));
		return text.ToString();
	}
}
