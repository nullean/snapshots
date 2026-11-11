using System.Runtime.CompilerServices;
using AwesomeAssertions;
using CsCheck;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nullean.Snapshots.Tests;

/// <summary>Finding, reading and rewriting the literal of an inline snapshot in C# source.</summary>
public class InlineSnapshotSourceTests
{
	private const string Method = "MatchInlineSnapshot";

	private static string Source(params string[] lines) => string.Join("\n", lines) + "\n";

	private static InlineEdit Edit(int line, string? expression, string? expected, string value, string method = Method) =>
		new(method, line, expression, expected, value);

	private static string Apply(string source, InlineEdit edit)
	{
		var result = InlineSnapshotSource.Rewrite(source, [edit]);
		result.Errors.Should().ContainSingle().Which.Should().BeNull();
		return result.Source;
	}

	private static string Error(string source, InlineEdit edit)
	{
		var result = InlineSnapshotSource.Rewrite(source, [edit]);
		result.Errors.Should().ContainSingle().Which.Should().NotBeNull();
		result.Source.Should().Be(source, "a refused edit changes nothing");
		return result.Errors[0]!;
	}

	[Test]
	public void ReplacesASingleLineLiteralWithARegularOne()
	{
		var source = Source("class T {", "\tvoid M()", "\t{", "\t\tx.Should().MatchInlineSnapshot(\"old\");", "\t}", "}");
		Apply(source, Edit(4, "\"old\"", "old", "new \"q\" \\ \t\n"))
			.Should()
			.Be(Source("class T {", "\tvoid M()", "\t{", "\t\tx.Should().MatchInlineSnapshot(\"new \\\"q\\\" \\\\ \\t\");", "\t}", "}"));
	}

	[Test]
	public void WritesSeveralLinesAsARawLiteralOneLevelPastTheStatement()
	{
		var source = Source("class T {", "\tvoid M()", "\t{", "\t\tx.Should().MatchInlineSnapshot(\"old\");", "\t}", "}");
		Apply(source, Edit(4, "\"old\"", "old", "a\n\nb {c}\n"))
			.Should()
			.Be(
				Source(
					"class T {",
					"\tvoid M()",
					"\t{",
					"\t\tx.Should().MatchInlineSnapshot(\"\"\"",
					"\t\t\ta",
					"",
					"\t\t\tb {c}",
					"\t\t\t\"\"\");",
					"\t}",
					"}"
				)
			);
	}

	[Test]
	public void IndentsOneLevelPastTheLineOfTheCallWithTheFilesStyle()
	{
		var spaces = Source("    x.Should()", "        .MatchInlineSnapshot(\"old\");");
		Apply(spaces, Edit(2, "\"old\"", "old", "a\nb\n"))
			.Should()
			.Be(Source("    x.Should()", "        .MatchInlineSnapshot(\"\"\"", "            a", "            b", "            \"\"\");"));

		var tabs = Source("\tvoid M() => x.Should()", "\t\t.MatchInlineSnapshot(\"old\");");
		Apply(tabs, Edit(2, "\"old\"", "old", "a\nb\n"))
			.Should()
			.Be(Source("\tvoid M() => x.Should()", "\t\t.MatchInlineSnapshot(\"\"\"", "\t\t\ta", "\t\t\tb", "\t\t\t\"\"\");"));
	}

	[Test]
	public void UsesMoreQuotesThanTheLongestRunInTheValue()
	{
		var source = Source("x.MatchInlineSnapshot(\"old\");");
		Apply(source, Edit(1, "\"old\"", "old", "say \"\"\"hi\"\"\"\nbye\n"))
			.Should()
			.Be(Source("x.MatchInlineSnapshot(\"\"\"\"", "    say \"\"\"hi\"\"\"", "    bye", "    \"\"\"\");"));
	}

	[Test]
	public void FallsBackToARegularLiteralWhenARawOneCannotHoldTheValue()
	{
		var source = Source("x.MatchInlineSnapshot(\"old\");");
		var written = Apply(source, Edit(1, "\"old\"", "old", "a\u0001\nb\u2028c\n"));
		written.Should().Be(Source("x.MatchInlineSnapshot(\"a\\u0001\\nb\\u2028c\");"));
	}

	[Test]
	public void WritesAnEmptyValueAsAnEmptyLiteral() =>
		Apply(Source("x.MatchInlineSnapshot(\"old\");"), Edit(1, "\"old\"", "old", string.Empty)).Should().Be(Source("x.MatchInlineSnapshot(\"\");"));

	[Test]
	public void InsertsTheLiteralWhenTheArgumentWasLeftOut() =>
		Apply(Source("x.Should().MatchInlineSnapshot();"), Edit(1, null, null, "new\n")).Should().Be(Source("x.Should().MatchInlineSnapshot(\"new\");"));

	[Test]
	public void AppendsANamedArgumentWhenOtherArgumentsAreGiven() =>
		Apply(Source("x.Should().MatchInlineSnapshot(because: \"why\");"), Edit(1, null, null, "new\n"))
			.Should()
			.Be(Source("x.Should().MatchInlineSnapshot(because: \"why\", expected: \"new\");"));

	[Test]
	public void ReplacesANullArgument() =>
		Apply(Source("x.Should().MatchInlineSnapshot(null);"), Edit(1, "null", null, "new\n")).Should().Be(Source("x.Should().MatchInlineSnapshot(\"new\");"));

	[Test]
	public void FindsANamedExpectedArgumentAmongOthers() =>
		Apply(Source("x.Should().MatchInlineSnapshot(because: \"old\", expected: \"old2\");"), Edit(1, "\"old2\"", "old2", "new\n"))
			.Should()
			.Be(Source("x.Should().MatchInlineSnapshot(because: \"old\", expected: \"new\");"));

	[Test]
	public void FindsTheFormatOverloadsSecondArgument() =>
		Apply(Source("x.Should().MatchInlineSnapshot(SnapshotFormats.Text, \"old\");"), Edit(1, "\"old\"", "old", "new\n"))
			.Should()
			.Be(Source("x.Should().MatchInlineSnapshot(SnapshotFormats.Text, \"new\");"));

	[Test]
	public void TellsTwoCallsOnOneLineApartByTheirArgument() =>
		Apply(Source("a.Should().MatchInlineSnapshot(\"one\"); b.Should().MatchInlineSnapshot(\"two\");"), Edit(1, "\"two\"", "two", "2\n"))
			.Should()
			.Be(Source("a.Should().MatchInlineSnapshot(\"one\"); b.Should().MatchInlineSnapshot(\"2\");"));

	[Test]
	public void IgnoresTheMethodNameInCommentsAndStrings()
	{
		var source = Source(
			"// x.MatchInlineSnapshot(\"old\")",
			"/* x.MatchInlineSnapshot(\"old\") */",
			"var s = \"x.MatchInlineSnapshot(\\\"old\\\")\"; var c = '\"';",
			"var r = \"\"\"",
			"    x.MatchInlineSnapshot(\"old\")",
			"    \"\"\";",
			"x.MatchInlineSnapshot(\"old\");"
		);
		var written = Apply(source, Edit(7, "\"old\"", "old", "new\n"));
		written.Should().Be(source.Replace("x.MatchInlineSnapshot(\"old\");\n", "x.MatchInlineSnapshot(\"new\");\n", StringComparison.Ordinal));
	}

	[Test]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	public void FindsACallSpreadOverSeveralLinesFromAnyOfItsLines(int reportedLine)
	{
		var source = Source("a.Should()", "\t.MatchInlineSnapshot(", "\t\t\"old\");");
		Apply(source, Edit(reportedLine, "\"old\"", "old", "new\n")).Should().Contain("\"new\"");
	}

	[Test]
	public void ReadsVerbatimAndRawLiterals()
	{
		InlineSnapshotSource.Check(Source("x.MatchInlineSnapshot(@\"a\"\"b\r\nc\");"), Edit(1, "@\"a\"\"b\r\nc\"", null, "x"), out var verbatim);
		verbatim.Should().Be("a\"b\r\nc");

		InlineSnapshotSource.Check(
			Source("  x.MatchInlineSnapshot(\"\"\"", "    one", "", "      two", "    \"\"\");"),
			Edit(1, "\"\"\"\n    one\n\n      two\n    \"\"\"", null, "x"),
			out var raw
		);
		raw.Should().Be("one\n\n  two");

		InlineSnapshotSource.Check(Source("x.MatchInlineSnapshot(\"\"\"say \"hi\"\"\"\");"), Edit(1, "\"\"\"say \"hi\"\"\"\"", null, "x"), out var single);
		single.Should().Be("say \"hi\"");

		InlineSnapshotSource.Check(
			Source("x.MatchInlineSnapshot(\"\\u0041\\x42\\U00000043\\n\\0\");"),
			Edit(1, "\"\\u0041\\x42\\U00000043\\n\\0\"", null, "x"),
			out var escapes
		);
		escapes.Should().Be("ABC\n\0");
	}

	[Test]
	public void KeepsTheFilesLineEndings()
	{
		var source = "class T\r\n{\r\n\tvoid M() { x.MatchInlineSnapshot(\"old\"); }\r\n}\r\n";
		Apply(source, Edit(3, "\"old\"", "old", "a\nb\n"))
			.Should()
			.Be("class T\r\n{\r\n\tvoid M() { x.MatchInlineSnapshot(\"\"\"\r\n\t\ta\r\n\t\tb\r\n\t\t\"\"\"); }\r\n}\r\n");
	}

	[Test]
	public void RefusesASourceThatChangedSinceTheRun()
	{
		var message = Error(Source("x.MatchInlineSnapshot(\"edited\");"), Edit(1, "\"edited\"", "old", "new\n"));
		message.Should().Contain("no longer holds the value");
	}

	[Test]
	public void ComparesTheLiteralAndTheRecordedValueNormalised() =>
		Apply(Source("x.MatchInlineSnapshot(\"a\\r\\nb\");"), Edit(1, "\"a\\r\\nb\"", "a\nb\n", "c\n")).Should().Contain("\"c\"");

	[Test]
	public void RefusesAnInterpolatedConstantOrConcatenatedExpected()
	{
		Error(Source("x.MatchInlineSnapshot($\"a{b}\");"), Edit(1, "$\"a{b}\"", "a", "n\n")).Should().Contain("not a plain string literal");
		Error(Source("x.MatchInlineSnapshot(Expected);"), Edit(1, "Expected", "a", "n\n")).Should().Contain("not a plain string literal");
		Error(Source("x.MatchInlineSnapshot(\"a\" + \"b\");"), Edit(1, "\"a\" + \"b\"", "ab", "n\n")).Should().Contain("not a plain string literal");
	}

	[Test]
	public void RefusesWhenNoCallMatches()
	{
		Error(Source("x.Other(\"old\");"), Edit(1, "\"old\"", "old", "n\n")).Should().Contain("no call to MatchInlineSnapshot");
		Error(Source("x.MatchInlineSnapshot(\"old\");"), Edit(9, "\"old\"", "old", "n\n")).Should().Contain("no call to MatchInlineSnapshot");
	}

	[Test]
	public void RefusesWhenTwoCallsMatch() =>
		Error(Source("a.MatchInlineSnapshot(\"old\"); b.MatchInlineSnapshot(\"old\");"), Edit(1, "\"old\"", "old", "n\n")).Should().Contain("2 calls");

	[Test]
	public void CasesThatDisagreeAboutOneLiteralAreAllRefusedAndCasesThatAgreeAreFine()
	{
		var source = Source("x.MatchInlineSnapshot(\"old\");");
		var disagree = InlineSnapshotSource.Rewrite(source, [Edit(1, "\"old\"", "old", "a\n"), Edit(1, "\"old\"", "old", "b\n")]);
		disagree.Source.Should().Be(source);
		disagree.Errors.Should().AllSatisfy(e => e.Should().Contain("different values"));

		var agree = InlineSnapshotSource.Rewrite(source, [Edit(1, "\"old\"", "old", "a\n"), Edit(1, "\"old\"", "old", "a\n")]);
		agree.Errors.Should().AllBeEquivalentTo((string?)null);
		agree.Source.Should().Be(Source("x.MatchInlineSnapshot(\"a\");"));
	}

	[Test]
	public void AppliesSeveralEditsInOneFileWithoutShiftingTheOthers()
	{
		var source = Source("a.MatchInlineSnapshot(\"1\");", "b.MatchInlineSnapshot(\"2\");", "c.MatchInlineSnapshot(\"3\");");
		var result = InlineSnapshotSource.Rewrite(
			source,
			[Edit(1, "\"1\"", "1", "one\ntwo\n"), Edit(3, "\"3\"", "3", "three\n"), Edit(2, "\"2\"", "x", "stale\n"),]
		);
		result.Errors.Select(e => e is null).Should().Equal(true, true, false);
		result
			.Source
			.Should()
			.Be(
				Source(
					"a.MatchInlineSnapshot(\"\"\"",
					"    one",
					"    two",
					"    \"\"\");",
					"b.MatchInlineSnapshot(\"2\");",
					"c.MatchInlineSnapshot(\"three\");"
				)
			);
	}

	[Test]
	public void ALexerErrorIsAnErrorNotACrash()
	{
		var act = () => InlineSnapshotSource.Rewrite("x.MatchInlineSnapshot(\"unterminated", [Edit(1, "\"unterminated", "u", "n\n")]);
		act.Should().Throw<InlineSnapshotException>();
	}

	// ---- what the real compiler reports -----------------------------------------------------------------------

	private sealed class Probe
	{
		public static Probe Instance { get; } = new();

		public Probe Chain() => this;
	}

	private sealed record Captured(int Line, string? Expression);

	private static Captured Capture(
		string? expected = null,
		[CallerArgumentExpression(nameof(expected))] string? expression = null,
		[CallerLineNumber] int line = 0
	)
	{
		_ = expected;
		return new(line, expression);
	}

	private static Captured Capture(
		Probe probe,
		string? expected = null,
		[CallerArgumentExpression(nameof(expected))] string? expression = null,
		[CallerLineNumber] int line = 0
	)
	{
		_ = probe;
		_ = expected;
		return new(line, expression);
	}

	private static Captured Capture2(
		string? expected = null,
		[CallerArgumentExpression(nameof(expected))] string? expression = null,
		[CallerLineNumber] int line = 0
	)
	{
		_ = expected;
		return new(line, expression);
	}

	private static string? ReadBack(Captured captured, string method, [CallerFilePath] string file = "")
	{
		var source = File.ReadAllText(file);
		InlineSnapshotSource.Check(source, new InlineEdit(method, captured.Line, captured.Expression, null, "x"), out var current);
		return current;
	}

	[Test]
	public void TheCompilersLineAndArgumentTextLocateTheLiteralInOurOwnSource()
	{
		// One line, a chain over several lines, a raw literal over several lines, and a named argument.
		ReadBack(Capture("one line"), nameof(Capture)).Should().Be("one line");

		ReadBack(Capture2("split over lines"), nameof(Capture2)).Should().Be("split over lines");

		ReadBack(Capture(Probe.Instance.Chain().Chain(), """
				several
				  lines
				"""), nameof(Capture))
			.Should()
			.Be("several\n  lines");

		ReadBack(Capture(expected: "named"), nameof(Capture)).Should().Be("named");

		ReadBack(Capture(), nameof(Capture)).Should().BeNull("the argument was left out");
	}

	// ---- properties --------------------------------------------------------------------------------------------

	private static readonly Gen<string> Values = Gen
		.OneOf(
			Gen.OneOf(
				"a",
				"b",
				" ",
				"  ",
				"\t",
				"\n",
				"\n\n",
				"\r\n",
				"\r",
				"\"",
				"\"\"\"",
				"\"\"\"\"",
				"\\",
				"{",
				"}",
				"$",
				"@",
				"//",
				"/*",
				"'",
				"\u0000",
				"\u0001",
				"\u007F",
				"\u0085",
				"\u00e9",
				"\ud83d\ude00",
				((char)0x2028).ToString(),
				"\u200b",
				"\ufeff"
			)
		)
		.List[0, 24]
		.Select(parts => SnapshotText.Normalize(string.Concat(parts)));

	private static readonly Gen<string> Indents = Gen.OneOf(string.Empty, "\t", "    ", "\t\t", "  ");

	[Test]
	public void WhatTheRewriterWritesIsTheValueWhenReadBack() =>
		Gen.Select(Values, Indents).Sample((value, indent) =>
		{
			var source = $"{indent}x.Should().MatchInlineSnapshot(\"old\");\n";
			var written = InlineSnapshotSource.Rewrite(source, [Edit(1, "\"old\"", "old", value)]);
			if (written.Errors[0] is not null)
				return false;
			var read = InlineSnapshotSource.Check(written.Source, Edit(1, null, null, "x") with { Expression = LiteralOf(written.Source) }, out var current);
			return read >= 0 && SnapshotText.Normalize(current ?? "\u0000null") == value;
		});

	[Test]
	public void RoslynReadsWhatTheRewriterWritesAsTheSameValue() =>
		Gen.Select(Values, Indents).Sample((value, indent) =>
		{
			var source = $"class T\n{{\n{indent}void M() => x.Should().MatchInlineSnapshot(\"old\");\n}}\n";
			var written = InlineSnapshotSource.Rewrite(source, [Edit(3, "\"old\"", "old", value)]);
			if (written.Errors[0] is not null)
				return false;
			var tree = CSharpSyntaxTree.ParseText(written.Source);
			if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
				return false;
			var literal = tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Single(l => l.IsKind(SyntaxKind.StringLiteralExpression));
			return (string)literal.Token.Value! == value.TrimEnd('\n');
		});

	[Test]
	public void OurOwnReaderAgreesWithRoslynOnLiteralsRoslynParses() =>
		Gen.Select(Values, Gen.OneOf(0, 1, 2)).Sample((value, form) =>
		{
			// Hand-written literal forms, not ones we wrote: regular with escapes, verbatim, and a raw literal.
			var body = value.TrimEnd('\n');
			var literal = form switch
			{
				0 =>
					"\"" + body
						.Replace("\\", "\\\\", StringComparison.Ordinal)
						.Replace("\"", "\\\"", StringComparison.Ordinal)
						.Replace("\n", "\\n", StringComparison.Ordinal)
						.Replace("\r", "\\r", StringComparison.Ordinal) + "\"",
				1 => "@\"" + body.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
				_ => InlineSnapshotSource.Encode(value, "  ", "\n"),
			};
			var source = $"class T {{ object o = x.MatchInlineSnapshot({literal}); }}\n";
			var tree = CSharpSyntaxTree.ParseText(source);
			if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
				return true; // not a literal the compiler accepts; nothing to agree on
			var roslyn =
				(string)tree
					.GetRoot()
					.DescendantNodes()
					.OfType<LiteralExpressionSyntax>()
					.Single(l => l.IsKind(SyntaxKind.StringLiteralExpression))
					.Token
					.Value!;
			InlineSnapshotSource.Check(source, Edit(1, literal, null, "x"), out var ours);
			return ours == roslyn.Replace("\r\n", "\n", StringComparison.Ordinal);
		});

	/// <summary>The source text of the one string literal in <paramref name="source"/>, as the compiler would report it.</summary>
	private static string LiteralOf(string source)
	{
		var tree = CSharpSyntaxTree.ParseText(source);
		return tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Single(l => l.IsKind(SyntaxKind.StringLiteralExpression)).ToString();
	}
}
