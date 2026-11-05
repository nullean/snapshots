using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using AwesomeAssertions.Primitives;

namespace Nullean.Snapshots.Html;

/// <summary>HTML assertions for AwesomeAssertions: snapshots and inline expected HTML.</summary>
public static class HtmlAssertions
{
	/// <summary>
	/// Asserts that the HTML matches its verified snapshot. The value is pretty-printed with
	/// <paramref name="html"/> applied, stored as <c>.verified.html</c>, and compared structurally.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="html">What to select and remove before printing.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers; they run on the pretty-printed HTML.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchHtmlSnapshot(
		this StringAssertions assertions,
		HtmlSnapshotOptions? html = null,
		string? name = null,
		SnapshotOptions? options = null,
		string because = "",
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		if (assertions.Subject is null)
			return FailNull(assertions);
		var pretty = HtmlSnapshot.Pretty(assertions.Subject, html);
		pretty.Should().MatchSnapshot(HtmlSnapshot.Format, name, options, because, callerFilePath, callerMemberName, callerLineNumber);
		return new AndConstraint<StringAssertions>(assertions);
	}

	/// <summary>
	/// Asserts that the HTML, pretty-printed with <paramref name="html"/> applied, equals the expected HTML written in the
	/// test as a string literal (a raw <c>"""</c> literal for several lines), compared structurally. Leave
	/// <paramref name="expected"/> out to write a new snapshot: the assertion fails, and <c>snap accept</c> (or
	/// <c>SNAPSHOT_ACCEPT=1</c>) writes the pretty-printed HTML into the source.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="expected">The expected HTML, as a literal.</param>
	/// <param name="html">What to select and remove before printing.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers; they run on the pretty-printed HTML.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="expectedExpression">Filled in by the compiler: how <paramref name="expected"/> reads in the source.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchInlineHtmlSnapshot(
		this StringAssertions assertions,
		[StringSyntax("html")] string? expected = null,
		HtmlSnapshotOptions? html = null,
		SnapshotOptions? options = null,
		string because = "",
		[CallerArgumentExpression(nameof(expected))] string? expectedExpression = null,
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		if (assertions.Subject is null)
			return FailNull(assertions);
		var site = new InlineSnapshotCallSite(new SnapshotCallSite(callerFilePath, callerMemberName, callerLineNumber), nameof(MatchInlineHtmlSnapshot), expectedExpression);
		return SnapshotAssertions.MatchInline(assertions, HtmlSnapshot.Pretty(assertions.Subject, html), expected, HtmlSnapshot.Format, site, options, because);
	}

	/// <summary>Asserts that the HTML is structurally equal to <paramref name="expected"/>, both normalised with <paramref name="html"/>.</summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="expected">The expected HTML.</param>
	/// <param name="html">What to select and remove before comparing; applied to both sides.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static AndConstraint<StringAssertions> BeHtml(this StringAssertions assertions, [StringSyntax("html")] string expected, HtmlSnapshotOptions? html = null, string because = "")
	{
		ArgumentNullException.ThrowIfNull(assertions);
		ArgumentNullException.ThrowIfNull(expected);
		if (assertions.Subject is null)
			return FailNull(assertions);
		var comparison = HtmlSnapshot.Compare(HtmlSnapshot.Pretty(expected, html), HtmlSnapshot.Pretty(assertions.Subject, html));
		var message = comparison.IsMatch
			? string.Empty
			: $"HTML did not match the expected HTML{Because(because)}:\n{SnapshotMessages.Describe(comparison)}";
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, comparison.IsMatch, message);
		return new AndConstraint<StringAssertions>(assertions);
	}

	/// <summary>
	/// Asserts that the pretty-printed HTML contains the pretty-printed <paramref name="expected"/>, ignoring
	/// indentation, so a fragment matches wherever it is nested.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="expected">The HTML fragment that must appear.</param>
	/// <param name="html">What to select and remove before comparing; applied to both sides.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static AndConstraint<StringAssertions> ContainHtml(this StringAssertions assertions, [StringSyntax("html")] string expected, HtmlSnapshotOptions? html = null, string because = "") =>
		Contain(assertions, expected, html, because, shouldContain: true);

	/// <summary>Asserts that the pretty-printed HTML does not contain the pretty-printed <paramref name="unexpected"/>.</summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="unexpected">The HTML fragment that must not appear.</param>
	/// <param name="html">What to select and remove before comparing; applied to both sides.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static AndConstraint<StringAssertions> NotContainHtml(this StringAssertions assertions, [StringSyntax("html")] string unexpected, HtmlSnapshotOptions? html = null, string because = "") =>
		Contain(assertions, unexpected, html, because, shouldContain: false);

	private static AndConstraint<StringAssertions> Contain(StringAssertions assertions, string fragment, HtmlSnapshotOptions? html, string because, bool shouldContain)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		ArgumentNullException.ThrowIfNull(fragment);
		if (assertions.Subject is null)
			return FailNull(assertions);

		var prettyFragment = HtmlSnapshot.Pretty(fragment, html);
		var prettyActual = HtmlSnapshot.Pretty(assertions.Subject, html);
		var found = Unindent(prettyActual).Contains(Unindent(prettyFragment), StringComparison.Ordinal);

		var message = found == shouldContain
			? string.Empty
			: new StringBuilder()
				.Append(shouldContain ? "Expected HTML to contain" : "Expected HTML not to contain").Append(Because(because)).Append(":\n")
				.Append(prettyFragment.TrimEnd('\n')).Append('\n')
				.Append(shouldContain ? "but it was not found in:\n" : "but it was found in:\n")
				.Append(Capped(prettyActual))
				.ToString();
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, found == shouldContain, message);
		return new AndConstraint<StringAssertions>(assertions);
	}

	private static string Unindent(string html) => html.Replace("\t", string.Empty, StringComparison.Ordinal);

	private static string Capped(string text)
	{
		var lines = text.TrimEnd('\n').Split('\n');
		return lines.Length <= SnapshotMessageLimits.MaxLines
			? string.Join('\n', lines)
			: string.Join('\n', lines.Take(SnapshotMessageLimits.MaxLines)) + $"\n(+{lines.Length - SnapshotMessageLimits.MaxLines} more lines)";
	}

	private static string Because(string because)
	{
		if (string.IsNullOrWhiteSpace(because))
			return string.Empty;
		because = because.Trim();
		return because.StartsWith("because ", StringComparison.OrdinalIgnoreCase) ? " " + because : " because " + because;
	}

	private static AndConstraint<StringAssertions> FailNull(StringAssertions assertions)
	{
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, "Expected HTML, but it was <null>.");
		return new AndConstraint<StringAssertions>(assertions);
	}
}
