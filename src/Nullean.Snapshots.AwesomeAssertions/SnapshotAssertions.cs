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
			because
		);

		FailUnless(chain, result.Passed, result.FailureMessage ?? string.Empty);
		return new AndConstraint<StringAssertions>(assertions);
	}

	/// <summary>
	/// Asserts that the string equals the expected value written in the test itself, as a string literal. Use a raw
	/// <c>"""</c> literal for several lines. Leave the argument out to write a new snapshot: the assertion fails, and
	/// <c>snap accept</c> (or <c>SNAPSHOT_ACCEPT=1</c>) writes the literal into the source. Works inside an
	/// <c>AssertionScope</c>.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="expected">
	/// The expected text, as a literal: <c>snap</c> edits the literal in the source, so a constant or an interpolated
	/// string compares but cannot be accepted. Normalised like a snapshot file (line endings, one trailing newline).
	/// </param>
	/// <param name="options">Per-call options such as scrubbers; they apply to the actual value only.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="expectedExpression">Filled in by the compiler: how <paramref name="expected"/> reads in the source.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchInlineSnapshot(
		this StringAssertions assertions,
		string? expected = null,
		SnapshotOptions? options = null,
		string because = "",
		[CallerArgumentExpression(nameof(expected))] string? expectedExpression = null,
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	) =>
		assertions.MatchInlineSnapshot(SnapshotFormats.Text, expected, options, because, expectedExpression, callerFilePath, callerMemberName, callerLineNumber);

	/// <summary>
	/// Asserts that the string equals the expected value written in the test, compared in the given
	/// <paramref name="format"/>. Format packages build their own inline assertions on <see cref="MatchInline"/>.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="format">How the value is compared.</param>
	/// <param name="expected">The expected text, as a literal.</param>
	/// <param name="options">Per-call options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="expectedExpression">Filled in by the compiler.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchInlineSnapshot(
		this StringAssertions assertions,
		SnapshotFormat format,
		string? expected = null,
		SnapshotOptions? options = null,
		string because = "",
		[CallerArgumentExpression(nameof(expected))] string? expectedExpression = null,
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		return MatchInline(
			assertions,
			assertions.Subject,
			expected,
			format,
			new InlineSnapshotCallSite(
				new SnapshotCallSite(callerFilePath, callerMemberName, callerLineNumber),
				nameof(MatchInlineSnapshot),
				expectedExpression
			),
			options,
			because
		);
	}

	/// <summary>
	/// The inline snapshot assertion for format packages: compares <paramref name="actual"/>, already prepared the way
	/// the format's own snapshot assertion prepares it (pretty-printed JSON, say), with the literal
	/// <paramref name="expected"/>. <paramref name="site"/> names the calling assertion method, which is searched for in
	/// the source, so a wrapper must be called directly from the test with <c>expected</c> as a parameter of that name.
	/// </summary>
	/// <param name="assertions">The assertions the failure is reported on.</param>
	/// <param name="actual">The value, or null when the subject was null.</param>
	/// <param name="expected">The literal's value, or null when the argument was left out.</param>
	/// <param name="format">How the value is compared.</param>
	/// <param name="site">Where the assertion is.</param>
	/// <param name="options">Per-call options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static AndConstraint<StringAssertions> MatchInline(
		StringAssertions assertions,
		string? actual,
		string? expected,
		SnapshotFormat format,
		InlineSnapshotCallSite site,
		SnapshotOptions? options,
		string because
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		ArgumentNullException.ThrowIfNull(format);
		var chain = assertions.CurrentAssertionChain;
		if (actual is null)
		{
			chain.FailWith("Expected a value to match a snapshot, but it was <null>.");
			return new AndConstraint<StringAssertions>(assertions);
		}

		var result = Snapshot.MatchInline(actual, expected, format, site, options, because);
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
