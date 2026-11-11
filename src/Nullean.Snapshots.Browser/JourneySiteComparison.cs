using System.Runtime.CompilerServices;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Differences;

namespace Nullean.Snapshots.Browser;

/// <summary>How <see cref="JourneySnapshot.CompareAsync(string, JourneyCompareOptions, CancellationToken, string)"/> runs a journey against two sites.</summary>
public sealed class JourneyCompareOptions
{
	/// <summary>The site to take the baseline from, such as production. Its root is written as <c>{origin}</c>.</summary>
	public required Uri Baseline { get; init; }

	/// <summary>The site to compare with the baseline, such as a preview.</summary>
	public required Uri Target { get; init; }

	/// <summary>Use the baseline of an earlier run, and run the baseline site only when there is none.</summary>
	public bool ReuseBaseline { get; init; }

	/// <summary>Append rules for unexplained differences to this <c>include?</c> file, a full path, instead of failing.</summary>
	public string? Record { get; init; }

	/// <summary>Remove the stale rules of this file, a full path.</summary>
	public string? Prune { get; init; }

	/// <summary>Everything else about the runs (hosts, timeouts, features, read-only, ...); its <c>BaseUrl</c> and <c>Transcript</c> are set per site.</summary>
	public JourneySnapshotOptions Run { get; init; } = new();
}

public static partial class JourneySnapshot
{
	/// <summary>
	/// Runs a journey against the baseline site, then against the target, in the shared Chrome, and compares the two
	/// transcripts under the rules in the journey's <c>.differences</c>. A step that fails on either side fails the result,
	/// naming the side. A relative <paramref name="journeyFile"/> is relative to the test project.
	/// </summary>
	/// <exception cref="JourneyParseException">The journey has errors.</exception>
	/// <exception cref="ExpectedDifferencesException">The rules cannot be read or are not valid.</exception>
	public static async Task<SiteComparisonResult> CompareAsync(
		string journeyFile,
		JourneyCompareOptions options,
		CancellationToken cancellationToken = default,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		ArgumentException.ThrowIfNullOrEmpty(journeyFile);
		ArgumentNullException.ThrowIfNull(options);
		var file = ResolveFile(journeyFile, callerFilePath);
		// One folder per site, so the target's run does not empty the baseline's screenshots. Only journeys that write files get one.
		var writes = options.Run.ArtifactsDirectory is null && (options.Run.ScreenshotOnFailure || WritesFiles(file));
		if (!options.ReuseBaseline || SiteComparison.ReadBaseline(Format, file) is null)
		{
			var baseline = await RunAsync(
				file,
				With(options.Run, writes ? SiteComparison.ArtifactsDirectory(file, "baseline") : null, options.Baseline),
				cancellationToken,
				callerFilePath
			).ConfigureAwait(false);
			if (baseline.Failure is { } failed)
				return Stopped("Baseline", failed, file);
			_ = SiteComparison.SeedBaseline(baseline.Text, Format, file);
		}

		var target = await RunAsync(
			file,
			With(options.Run, writes ? SiteComparison.ArtifactsDirectory(file, "target") : null, options.Target),
			cancellationToken,
			callerFilePath
		).ConfigureAwait(false);
		return Compare(target, options.Target, file, options);
	}

	/// <summary>
	/// The screenshots of the journey's last <see cref="CompareAsync"/> that differ between the two sites, each with a diff image
	/// written next to the target's. Screenshots come from the journey's <c>screenshot</c> steps.
	/// </summary>
	/// <param name="journeyFile">The journey; a relative path is relative to the test project.</param>
	/// <param name="options">How closely the pixels must match; image snapshots' defaults when unset.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	public static IReadOnlyList<ImageDifference> CompareScreenshots(
		string journeyFile,
		Images.ImageSnapshotOptions? options = null,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		ArgumentException.ThrowIfNullOrEmpty(journeyFile);
		var file = ResolveFile(journeyFile, callerFilePath);
		return SiteImages.Compare(
			new System.IO.Abstractions.FileSystem(),
			SiteComparison.ArtifactsDirectory(file, "baseline", clean: false),
			SiteComparison.ArtifactsDirectory(file, "target", clean: false),
			options
		);
	}

	/// <summary>Compares a target's transcript with the seeded baseline, as <see cref="CompareAsync"/> does after its two runs.</summary>
	internal static SiteComparisonResult Compare(JourneyTranscript target, Uri targetSite, string file, JourneyCompareOptions options)
	{
		var result = SiteComparison.Compare(
			target.Text,
			Format,
			JourneyRuleTargets.Instance,
			file,
			new SiteComparisonOptions { Record = options.Record, Prune = options.Prune, Note = $"recorded against {targetSite}", }
		);
		if (target.Failure is not { } stopped)
			return result;
		var message = $"Target stopped at {stopped.Describe(Relative(file))}";
		return result with { Passed = false, FailureMessage = result.FailureMessage is { } m ? message + "\n" + m : message };
	}

	private static SiteComparisonResult Stopped(string side, JourneyFailure failure, string file) =>
		new(false, $"{side} stopped at {failure.Describe(Relative(file))}", new RuleApplication([], [], [], []), string.Empty, null, [], 0);
}
