using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AwesomeAssertions.Primitives;

using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Http;

/// <summary>AwesomeAssertions extensions for HTTP responses and <c>.http</c> file runs.</summary>
public static class HttpAssertions
{
	/// <summary>
	/// Asserts that a response matches its verified snapshot (<c>.verified.responses</c>), keyed by the test like any
	/// snapshot: the status, the chosen headers and the decoded body, a JSON body compared as a tree.
	/// </summary>
	/// <param name="assertions">Assertions on an <see cref="HttpResponseMessage"/>.</param>
	/// <param name="http">Which headers to write.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="diff">How trees are compared: ignored paths such as <c>$[*].body.items[*].score</c>, strict numbers.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static async Task<AndConstraint<ObjectAssertions>> MatchHttpSnapshot(
		this ObjectAssertions assertions,
		HttpSnapshotOptions? http = null,
		string? name = null,
		SnapshotOptions? options = null,
		string because = "",
		TreeDiffOptions? diff = null,
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		var chain = assertions.CurrentAssertionChain;
		if (assertions.Subject is not HttpResponseMessage response)
		{
			SnapshotAssertions.FailUnless(
				chain,
				false,
				$"Expected an HttpResponseMessage, but found {(assertions.Subject is null ? "<null>" : assertions.Subject.GetType().Name)}."
			);
			return new AndConstraint<ObjectAssertions>(assertions);
		}

		var text = await HttpSnapshot.RenderAsync(response, http).ConfigureAwait(false);
		var result = Snapshot.Match(
			text,
			HttpSnapshot.FormatWith(diff),
			new SnapshotCallSite(callerFilePath, callerMemberName, callerLineNumber),
			name,
			options,
			because
		);
		SnapshotAssertions.FailUnless(chain, result.Passed, result.FailureMessage ?? string.Empty);
		return new AndConstraint<ObjectAssertions>(assertions);
	}

	/// <summary>
	/// Asserts that responses text, as <see cref="HttpSnapshot.RenderAsync"/> writes it, matches its verified snapshot
	/// (<c>.verified.responses</c>), compared section by section.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="diff">How trees are compared: ignored paths such as <c>$[*].body.items[*].score</c>, strict numbers.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchHttpSnapshot(
		this StringAssertions assertions,
		string? name = null,
		SnapshotOptions? options = null,
		string because = "",
		TreeDiffOptions? diff = null,
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		if (assertions.Subject is not null)
		{
			try
			{
				_ = HttpSnapshot.Format.Parse(assertions.Subject);
			}
			catch (FormatException e)
			{
				SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, $"Expected HTTP responses text, but: {e.Message}");
				return new AndConstraint<StringAssertions>(assertions);
			}
		}

		return assertions.MatchSnapshot(HttpSnapshot.FormatWith(diff), name, options, because, callerFilePath, callerMemberName, callerLineNumber);
	}

	/// <summary>
	/// Asserts that a <c>.http</c> run's transcript matches the file's verified snapshot,
	/// <c>.snapshots/{file path}.verified.responses</c>. A run that stopped at a failing request fails, naming the
	/// request first.
	/// </summary>
	/// <param name="assertions">Assertions on an <see cref="HttpTranscript"/>.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="diff">How trees are compared: ignored paths such as <c>$[*].body.items[*].score</c>, strict numbers.</param>
	public static AndConstraint<ObjectAssertions> MatchHttpFileSnapshot(
		this ObjectAssertions assertions,
		SnapshotOptions? options = null,
		string because = "",
		TreeDiffOptions? diff = null
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		if (assertions.Subject is not HttpTranscript transcript)
		{
			SnapshotAssertions.FailUnless(
				assertions.CurrentAssertionChain,
				false,
				$"Expected a transcript from HttpFileSnapshot.RunAsync, but found {(assertions.Subject is null ? "<null>" : assertions.Subject.GetType().Name)}."
			);
			return new AndConstraint<ObjectAssertions>(assertions);
		}

		var result = HttpFileSnapshot.Match(transcript, options, because, diff);
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, result.Passed, result.FailureMessage ?? string.Empty);
		return new AndConstraint<ObjectAssertions>(assertions);
	}
}
