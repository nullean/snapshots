using System.Runtime.CompilerServices;
using AwesomeAssertions;

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
