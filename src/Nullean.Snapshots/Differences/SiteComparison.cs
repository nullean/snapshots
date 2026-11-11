using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Differences;

/// <summary>How <see cref="SiteComparison.Compare"/> reads and writes rules.</summary>
public sealed class SiteComparisonOptions
{
	/// <summary>Keeps a separate seeded baseline for a variant of the file, as <c>MatchFile</c>'s variants do.</summary>
	public string? Variant { get; init; }

	/// <summary>
	/// Appends a rule for every unexplained difference to this file, which must be an <c>include?</c> of the file's
	/// rules, and then does not fail for them. A full path.
	/// </summary>
	public string? Record { get; init; }

	/// <summary>Removes the stale rules of this file (a full path), and does not fail for them.</summary>
	public string? Prune { get; init; }

	/// <summary>What the target is, written in the comment above recorded rules: <c>recorded against https://preview.example.com</c>.</summary>
	public string? Note { get; init; }
}

/// <summary>The outcome of comparing a target with a baseline under the rules.</summary>
/// <param name="Passed">Everything that differs is explained by a rule (or was recorded), and no rule is stale (or was pruned).</param>
/// <param name="FailureMessage">Why not, in plain text: the unexplained differences, stale rules, suggested rules and where the files are.</param>
/// <param name="Application">What the rules made of the differences.</param>
/// <param name="BaselinePath">The seeded baseline, full path.</param>
/// <param name="TargetPath">The target's received file, full path; written only when the comparison did not pass.</param>
/// <param name="Recorded">The rule lines appended to <see cref="SiteComparisonOptions.Record"/>.</param>
/// <param name="Pruned">How many stale rules were removed from <see cref="SiteComparisonOptions.Prune"/>.</param>
public sealed record SiteComparisonResult(
	bool Passed,
	string? FailureMessage,
	RuleApplication Application,
	string BaselinePath,
	string? TargetPath,
	IReadOnlyList<string> Recorded,
	int Pruned
);

/// <summary>
/// Compares a run against site X with the same run against site Y. The baseline is seeded into the received tree
/// (<c>{name}.baseline.{ext}</c>, never checked in, never a snapshot of its own), the target is compared with it, and the
/// differences are judged by the checked-in rules of <c>{name}.differences</c>.
/// </summary>
public static class SiteComparison
{
	/// <summary>Writes the baseline's text where the comparison finds it; returns its full path.</summary>
	public static string SeedBaseline(string text, SnapshotFormat format, string sourceFile, string? variant = null) =>
		Snapshot.Current.SeedBaseline(text, format.Extension, sourceFile, variant);

	/// <summary>
	/// The folder a run against one site writes its screenshots and HAR files into, <c>{name}.{role}.artifacts</c> in the
	/// received tree (<paramref name="role"/> is <c>baseline</c> or <c>target</c>). <paramref name="clean"/> empties it first.
	/// </summary>
	public static string ArtifactsDirectory(string sourceFile, string role, bool clean = true) =>
		Snapshot.Current.SiteArtifactsDirectory(sourceFile, role, clean);

	/// <summary>The seeded baseline, or <see langword="null"/> when there is none.</summary>
	public static string? ReadBaseline(SnapshotFormat format, string sourceFile, string? variant = null) =>
		Snapshot.Current.ReadBaseline(format.Extension, sourceFile, variant);

	/// <summary>Compares <paramref name="targetText"/> with the seeded baseline of <paramref name="sourceFile"/>.</summary>
	/// <exception cref="InvalidOperationException">There is no seeded baseline, or <see cref="SiteComparisonOptions.Record"/> is not an <c>include?</c> of the rules.</exception>
	/// <exception cref="ExpectedDifferencesException">The rules cannot be read or are not valid.</exception>
	public static SiteComparisonResult Compare(
		string targetText,
		TreeSnapshotFormat format,
		RuleTargets targets,
		string sourceFile,
		SiteComparisonOptions? options = null
	) => Snapshot.Current.CompareSites(targetText, format, targets, sourceFile, options ?? new SiteComparisonOptions());
}
