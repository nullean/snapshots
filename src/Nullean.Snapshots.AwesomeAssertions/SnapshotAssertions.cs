using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using AwesomeAssertions.Formatting;
using AwesomeAssertions.Primitives;

namespace Nullean.Snapshots;

/// <summary>Snapshot assertions for AwesomeAssertions.</summary>
public static class SnapshotAssertions
{
	static SnapshotAssertions() => Formatter.AddFormatter(new VerbatimMessageFormatter());

	/// <summary>
	/// Asserts that the string matches its verified snapshot, stored as text. On a mismatch the received file is
	/// written and the failure message says how to accept it. Works inside an <c>AssertionScope</c>.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchSnapshot(
		this StringAssertions assertions,
		string? name = null,
		SnapshotOptions? options = null,
		string because = "",
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	) => assertions.MatchSnapshot(SnapshotFormats.Text, name, options, because, callerFilePath, callerMemberName, callerLineNumber);

	/// <summary>
	/// Asserts that the string matches its verified snapshot in the given <paramref name="format"/>. Format packages
	/// build their own assertions on this.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="format">How the value is stored and compared.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchSnapshot(
		this StringAssertions assertions,
		SnapshotFormat format,
		string? name = null,
		SnapshotOptions? options = null,
		string because = "",
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		ArgumentNullException.ThrowIfNull(format);
		var chain = assertions.CurrentAssertionChain;
		if (assertions.Subject is null)
		{
			chain.FailWith("Expected a value to match a snapshot, but it was <null>.");
			return new AndConstraint<StringAssertions>(assertions);
		}

		var result = Snapshot.Match(
			assertions.Subject,
			format,
			new SnapshotCallSite(callerFilePath, callerMemberName, callerLineNumber),
			name,
			options,
			because);

		FailUnless(chain, result.Passed, result.FailureMessage ?? string.Empty);
		return new AndConstraint<StringAssertions>(assertions);
	}

	/// <summary>
	/// Fails <paramref name="chain"/> with <paramref name="message"/> shown exactly as given, unless
	/// <paramref name="condition"/> holds. For format packages building their own assertions.
	/// </summary>
	/// <param name="chain">The assertion chain, e.g. <c>assertions.CurrentAssertionChain</c>.</param>
	/// <param name="condition">The assertion passes when this is true.</param>
	/// <param name="message">The plain-text failure message.</param>
	public static void FailUnless(AssertionChain chain, bool condition, string message)
	{
		ArgumentNullException.ThrowIfNull(chain);
		ArgumentNullException.ThrowIfNull(message);
		// The message goes in as an argument, never as the format string: AwesomeAssertions reads {braces} in a format
		// string as placeholders, and snapshot content is full of them.
		chain.ForCondition(condition).FailWith("{0}", new VerbatimMessage(message));
	}

	/// <summary>A message the formatter writes as is: no quotes, no escaping.</summary>
	internal sealed record VerbatimMessage(string Text);

	private sealed class VerbatimMessageFormatter : IValueFormatter
	{
		public bool CanHandle(object value) => value is VerbatimMessage;

		public void Format(object value, FormattedObjectGraph formattedGraph, FormattingContext context, FormatChild formatChild) =>
			formattedGraph.AddFragment(((VerbatimMessage)value).Text);
	}
}
