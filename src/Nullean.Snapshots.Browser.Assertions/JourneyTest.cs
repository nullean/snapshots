using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Snapshots.Browser;

/// <summary>Runs a journey and asserts its snapshot in one call, for a data-driven test over journey files.</summary>
public static class JourneyTest
{
	/// <summary>
	/// Runs <paramref name="journey"/> and asserts that its transcript matches the verified snapshot. A journey that
	/// stopped at a failing step fails, naming the step first.
	/// </summary>
	/// <param name="journey">A file from a data source such as Nullean.Snapshots.TUnit's <c>[JourneyFiles]</c>.</param>
	/// <param name="options">How to run it: the base URL, allowed hosts, secrets.</param>
	/// <param name="cancellationToken">Stops the run.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	public static Task RunAsync(
		SnapshotFile journey,
		JourneySnapshotOptions? options = null,
		CancellationToken cancellationToken = default,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		ArgumentNullException.ThrowIfNull(journey);
		return RunAsync(journey.Path, options, cancellationToken, callerFilePath);
	}

	/// <summary>
	/// Why <paramref name="journey"/> should not run in <paramref name="context"/>, or <c>null</c> when it applies. A test
	/// framework skips with it before <c>RunAsync</c>, for example in TUnit: <c>if (JourneyTest.Unmet(journey, context) is { } why) Skip.Test(why);</c>
	/// </summary>
	/// <param name="journey">A file from a data source such as Nullean.Snapshots.TUnit's <c>[JourneyFiles]</c>.</param>
	/// <param name="context">What the run knows about the site: its feature flags, build type and environment.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	public static string? Unmet(SnapshotFile journey, JourneyContext context, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentNullException.ThrowIfNull(journey);
		return JourneySnapshot.Unmet(journey.Path, context, callerFilePath);
	}

	/// <summary>As <see cref="Unmet(SnapshotFile, JourneyContext, string)"/>, and also <c>@mutates</c> on a read-only run, judged from the run's options.</summary>
	/// <param name="journey">A file from a data source such as <c>[JourneyFiles]</c>.</param>
	/// <param name="options">The run's options, including its flags and whether it is read-only.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	public static string? SkipReason(SnapshotFile journey, JourneySnapshotOptions options, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentNullException.ThrowIfNull(journey);
		return JourneySnapshot.SkipReason(journey.Path, options, callerFilePath);
	}

	/// <summary>As <see cref="RunAsync(SnapshotFile, JourneySnapshotOptions?, CancellationToken, string)"/>, for a path relative to the test project.</summary>
	public static async Task RunAsync(
		string journey,
		JourneySnapshotOptions? options = null,
		CancellationToken cancellationToken = default,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		var transcript = await JourneySnapshot.RunAsync(journey, options, cancellationToken, callerFilePath).ConfigureAwait(false);
		transcript.Should().MatchJourneySnapshot();
	}
}
