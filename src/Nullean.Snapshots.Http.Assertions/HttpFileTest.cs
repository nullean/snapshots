using System.Runtime.CompilerServices;
using AwesomeAssertions;

namespace Nullean.Snapshots.Http;

/// <summary>Runs a <c>.http</c> file and asserts its snapshot in one call, for a data-driven test over <c>.http</c> files.</summary>
public static class HttpFileTest
{
	/// <summary>
	/// Runs <paramref name="file"/> and asserts that its transcript matches the verified snapshot. A run that stopped
	/// at a failing request fails, naming the request first.
	/// </summary>
	/// <param name="file">A file from a data source such as Nullean.Snapshots.TUnit's <c>[HttpFiles]</c>.</param>
	/// <param name="options">How to run it: the client, environment, variables and secrets.</param>
	/// <param name="cancellationToken">Stops the run.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	public static Task RunAsync(
		SnapshotFile file,
		HttpFileSnapshotOptions? options = null,
		CancellationToken cancellationToken = default,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		ArgumentNullException.ThrowIfNull(file);
		return RunAsync(file.Path, options, cancellationToken, callerFilePath);
	}

	/// <summary>As <see cref="RunAsync(SnapshotFile, HttpFileSnapshotOptions?, CancellationToken, string)"/>, for a path relative to the test project.</summary>
	public static async Task RunAsync(
		string file,
		HttpFileSnapshotOptions? options = null,
		CancellationToken cancellationToken = default,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		var transcript = await HttpFileSnapshot.RunAsync(file, options, cancellationToken, callerFilePath).ConfigureAwait(false);
		transcript.Should().MatchHttpFileSnapshot();
	}
}
