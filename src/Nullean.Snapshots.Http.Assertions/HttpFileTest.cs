using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Nullean.HttpFileParser;

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

	/// <summary>
	/// Why <paramref name="file"/> should not run in <paramref name="context"/>, or <see langword="null"/> when it applies. A
	/// test framework skips with it before <c>RunAsync</c>, for example in TUnit:
	/// <c>if (HttpFileTest.Unmet(file, context) is { } why) Skip.Test(why);</c>
	/// </summary>
	/// <param name="file">A file from a data source such as <c>[HttpFiles]</c>.</param>
	/// <param name="context">What the run knows about the service: its feature flags, build type and environment.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	public static string? Unmet(SnapshotFile file, HttpFileContext context, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentNullException.ThrowIfNull(file);
		return HttpFileSnapshot.Unmet(file.Path, context, callerFilePath);
	}

	/// <summary>As <see cref="Unmet(SnapshotFile, HttpFileContext, string)"/>, and also <c># @mutates</c> on a read-only run, judged from the run's options.</summary>
	/// <param name="file">A file from a data source such as <c>[HttpFiles]</c>.</param>
	/// <param name="options">The run's options, including its flags and whether it is read-only.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	public static string? SkipReason(SnapshotFile file, HttpFileSnapshotOptions options, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentNullException.ThrowIfNull(file);
		return HttpFileSnapshot.SkipReason(file.Path, options, callerFilePath);
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
