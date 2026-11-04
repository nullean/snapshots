using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AwesomeAssertions.Primitives;

namespace Nullean.Snapshots.Browser;

/// <summary>AwesomeAssertions extensions for browser journeys and accessibility trees.</summary>
public static class BrowserAssertions
{
	/// <summary>
	/// Asserts that a journey's transcript matches the journey's verified snapshot,
	/// <c>.snapshots/{journey path}.verified.journey</c>. A journey that stopped at a failing step fails, naming the
	/// step first.
	/// </summary>
	/// <param name="assertions">Assertions on a <see cref="JourneyTranscript"/>.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static AndConstraint<ObjectAssertions> MatchJourneySnapshot(this ObjectAssertions assertions, SnapshotOptions? options = null, string because = "")
	{
		ArgumentNullException.ThrowIfNull(assertions);
		if (assertions.Subject is not JourneyTranscript transcript)
		{
			SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false,
				$"Expected a journey transcript from JourneySnapshot.RunAsync, but found {(assertions.Subject is null ? "<null>" : assertions.Subject.GetType().Name)}.");
			return new AndConstraint<ObjectAssertions>(assertions);
		}

		var result = JourneySnapshot.Match(transcript, options, because);
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, result.Passed, result.FailureMessage ?? string.Empty);
		return new AndConstraint<ObjectAssertions>(assertions);
	}

	/// <summary>
	/// Asserts that an accessibility tree, as Nullean.Cdp's <c>SnapshotAsync</c> prints it, matches its verified
	/// snapshot (<c>.verified.aria</c>), compared node by node.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchAriaSnapshot(
		this StringAssertions assertions,
		string? name = null,
		SnapshotOptions? options = null,
		string because = "",
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
				_ = AriaTree.Parse(assertions.Subject);
			}
			catch (FormatException e)
			{
				SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, $"Expected an accessibility tree, but: {e.Message}");
				return new AndConstraint<StringAssertions>(assertions);
			}
		}

		return assertions.MatchSnapshot(JourneySnapshot.AriaFormat, name, options, because, callerFilePath, callerMemberName, callerLineNumber);
	}
}
