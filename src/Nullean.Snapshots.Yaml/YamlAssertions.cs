using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AwesomeAssertions.Primitives;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Yaml;

/// <summary>YAML assertions for AwesomeAssertions: snapshots of YAML text and inline YAML.</summary>
public static class YamlAssertions
{
	/// <summary>
	/// Asserts that the YAML text matches its verified snapshot. The text is stored as written (comments survive,
	/// nothing is re-emitted) as <c>.verified.yml</c> and compared as a tree.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="diff">How trees are compared: key order, strict numbers, ignored paths.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchYamlSnapshot(
		this StringAssertions assertions,
		TreeDiffOptions? diff = null,
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
		try
		{
			_ = YamlSnapshot.Parse(assertions.Subject);
		}
		catch (FormatException e)
		{
			SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, $"Expected valid YAML{Because(because)}, but: {e.Message}");
			return new AndConstraint<StringAssertions>(assertions);
		}

		assertions.Subject.Should().MatchSnapshot(YamlSnapshot.FormatWith(diff), name, options, because, callerFilePath, callerMemberName, callerLineNumber);
		return new AndConstraint<StringAssertions>(assertions);
	}

	/// <summary>
	/// Asserts that the YAML text equals the expected YAML written in the test as a string literal (a raw <c>"""</c>
	/// literal for several lines), compared as a tree. The text is written as given when accepted, comments included.
	/// Leave <paramref name="expected"/> out to write a new snapshot: the assertion fails, and <c>snap accept</c> (or
	/// <c>SNAPSHOT_ACCEPT=1</c>) writes the literal into the source.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="expected">The expected YAML, as a literal.</param>
	/// <param name="diff">How trees are compared: key order, strict numbers, ignored paths.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="expectedExpression">Filled in by the compiler: how <paramref name="expected"/> reads in the source.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchInlineYamlSnapshot(
		this StringAssertions assertions,
		[StringSyntax("yaml")] string? expected = null,
		TreeDiffOptions? diff = null,
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
		try
		{
			_ = YamlSnapshot.Parse(assertions.Subject);
		}
		catch (FormatException e)
		{
			SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, $"Expected valid YAML{Because(because)}, but: {e.Message}");
			return new AndConstraint<StringAssertions>(assertions);
		}

		var site = new InlineSnapshotCallSite(new SnapshotCallSite(callerFilePath, callerMemberName, callerLineNumber), nameof(MatchInlineYamlSnapshot), expectedExpression);
		return SnapshotAssertions.MatchInline(assertions, assertions.Subject, expected, YamlSnapshot.FormatWith(diff), site, options, because);
	}

	/// <summary>Asserts that the YAML text equals <paramref name="expected"/> as a tree.</summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="expected">The expected YAML.</param>
	/// <param name="diff">How trees are compared.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static AndConstraint<StringAssertions> BeYaml(this StringAssertions assertions, [StringSyntax("yaml")] string expected, TreeDiffOptions? diff = null, string because = "")
	{
		ArgumentNullException.ThrowIfNull(assertions);
		ArgumentNullException.ThrowIfNull(expected);
		if (assertions.Subject is null)
			return FailNull(assertions);

		var format = YamlSnapshot.FormatWith(diff);
		SnapshotComparison comparison;
		try
		{
			_ = format.Parse(assertions.Subject);
			comparison = format.Compare(expected, assertions.Subject);
		}
		catch (FormatException e)
		{
			SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, $"Expected valid YAML{Because(because)}, but: {e.Message}");
			return new AndConstraint<StringAssertions>(assertions);
		}

		var message = comparison.IsMatch
			? string.Empty
			: $"YAML did not match the expected YAML{Because(because)}:\n{SnapshotMessages.Describe(comparison)}";
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, comparison.IsMatch, message);
		return new AndConstraint<StringAssertions>(assertions);
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
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, "Expected YAML, but it was <null>.");
		return new AndConstraint<StringAssertions>(assertions);
	}
}
