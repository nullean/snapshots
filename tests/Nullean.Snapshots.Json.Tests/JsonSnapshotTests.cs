using System.Text.Json.Serialization;
using AwesomeAssertions;
using Nullean.Snapshots.Tests;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Json.Tests;

public class JsonSnapshotTests
{
	private static IReadOnlyList<TreeDifference> Diff(string expected, string actual, TreeDiffOptions? options = null) =>
		TreeDiffer.Instance.Diff(JsonSnapshot.Parse(expected), JsonSnapshot.Parse(actual), options);

	private static string Lines(string expected, string actual, TreeDiffOptions? options = null) =>
		string.Join("\n", TreeDifferences.Describe(Diff(expected, actual, options), "verified.json").Select(l => l[0]));

	[Test]
	public void PrettyKeepsOrderNumbersAndUnicode() =>
		JsonSnapshot
			.Pretty("""{"b":1.50,"a":[1e3,"é <tag> 'q'"],"c":{}}""")
			.Should()
			.Be("{\n  \"b\": 1.50,\n  \"a\": [\n    1e3,\n    \"é <tag> 'q'\"\n  ],\n  \"c\": {}\n}\n");

	[Test]
	public void DuplicatePropertiesAreAnError()
	{
		var act = () => JsonSnapshot.Parse("{\n  \"a\": 1,\n  \"a\": 2\n}");
		act.Should().Throw<FormatException>().WithMessage("The property \"a\" appears twice in one object, at 3:3.");
	}

	[Test]
	public void InvalidJsonIsAFormatException()
	{
		var act = () => JsonSnapshot.Parse("{\"a\": }");
		act.Should().Throw<FormatException>();
	}

	[Test]
	public void PositionsAreLineAndColumnInCharacters()
	{
		var tree = (MapNode)JsonSnapshot.Parse("{\n  \"é\": \"x\",\n  \"b\": [1,\n    2]\n}");
		tree.Span.Should().Be(new SourceSpan(1, 1));
		tree.Entries[0].Value.Span.Should().Be(new SourceSpan(2, 8));
		((SeqNode)tree.Entries[1].Value).Items[1].Span.Should().Be(new SourceSpan(4, 5));
	}

	[Test]
	[Arguments("1", "1.0", true)]
	[Arguments("10", "1e1", true)]
	[Arguments("-0", "0", true)]
	[Arguments("0.10", "0.1", true)]
	[Arguments("12345678901234567890", "12345678901234567891", false)]
	[Arguments("1e300", "1.0E+300", true)]
	[Arguments("1", "2", false)]
	public void NumbersCompareByValue(string a, string b, bool equal)
	{
		Diff(a, b).Should().HaveCount(equal ? 0 : 1);
		Diff(a, b, new TreeDiffOptions { StrictNumbers = true }).Should().HaveCount(a == b ? 0 : 1);
	}

	[Test]
	[Arguments(".inf", "Infinity", true)]
	[Arguments("-.inf", ".inf", false)]
	[Arguments(".nan", "NaN", true)]
	[Arguments("1.5", "15e-1", true)]
	[Arguments("1.", "1", true)]
	[Arguments("abc", "", false)]
	public void NumberValues(string a, string b, bool equal)
	{
		var parsedA = NumberValue.TryParse(a, out var x);
		var parsedB = NumberValue.TryParse(b, out var y);
		(parsedA && parsedB && x == y).Should().Be(equal);
	}

	[Test]
	public void PathsQuoteKeysThatAreNotIdentifiers()
	{
		Lines("""{"a b":{"c.d":[1]}}""", """{"a b":{"c.d":[2]}}""").Should().Be("""$["a b"]["c.d"][0]   expected 1   actual 2   (verified.json:1:16)""");
		NodePath
			.Parse("""$["a b"]["c.d"][0].e""")
			.Should()
			.Equal(PathSegment.ForKey("a b"), PathSegment.ForKey("c.d"), PathSegment.ForIndex(0), PathSegment.ForKey("e"));
	}

	[Test]
	public void ChangedItemFieldIsReportedByItsPath() =>
		Lines("""{"items":[{"id":1,"name":"beats"},{"id":2,"name":"kibana"}]}""", """{"items":[{"id":1,"name":"beats"},{"id":2,"name":"Kibana"}]}""")
			.Should()
			.Be("""$.items[1].name   expected "kibana"   actual "Kibana"   (verified.json:1:50)""");

	[Test]
	public void InsertedItemShiftsTheActualIndex() =>
		Lines("""[{"id":1,"v":"a"},{"id":2,"v":"b"}]""", """[{"id":0,"v":"new"},{"id":1,"v":"a"},{"id":2,"v":"B"}]""")
			.Should()
			.Be(
				"""
				$[0]                     unexpected in actual
				$[1].v (actual $[2].v)   expected "b"   actual "B"   (verified.json:1:31)
				""".ReplaceLineEndings(
					"\n"
				)
			);

	[Test]
	public void MissingUnexpectedAndTypeChanges() =>
		Lines("""{"a":1,"b":"1","c":[1]}""", """{"b":1,"c":{"x":1},"d":null}""")
			.Should()
			.Be(
				"""
			$.a   missing in actual   (verified.json:1:6)
			$.b   expected (string) "1"   actual (int) 1   (verified.json:1:12)
			$.c   expected (sequence) […1 items]   actual (map) {…1 keys}   (verified.json:1:20)
			$.d   unexpected in actual
			""".ReplaceLineEndings(
					"\n"
				)
			);

	[Test]
	public void KeyOrderOnlyWhenItMatters()
	{
		Diff("""{"a":1,"b":2}""", """{"b":2,"a":1}""").Should().BeEmpty();
		Lines("""{"a":1,"b":2}""", """{"b":2,"a":1}""", new TreeDiffOptions { MapOrder = MapOrder.Significant })
			.Should()
			.Be("$   key order changed: expected [a, b]   actual [b, a]   (verified.json:1:1)");
	}

	[Test]
	public void IgnoredPathsAreLeftOut()
	{
		var options = new TreeDiffOptions { IgnorePaths = ["$.meta.generatedAt", "$..id", "$.items[*].etag"] };
		Diff(
			"""{"meta":{"generatedAt":"x","v":1},"items":[{"id":1,"etag":"a","n":1}],"deep":{"id":5}}""",
			"""{"meta":{"generatedAt":"y","v":1},"items":[{"id":2,"etag":"b","n":1}],"deep":{"id":6}}""",
			options
		).Should().BeEmpty();
		Diff("""{"items":[1,2]}""", """{"items":[1,2,3]}""", new TreeDiffOptions { IgnorePaths = ["$.items[*]"] }).Should().BeEmpty();
	}

	[Test]
	public void NullCanEqualMissing()
	{
		Diff("""{"a":1,"b":null}""", """{"a":1}""").Should().HaveCount(1);
		Diff("""{"a":1,"b":null}""", """{"a":1,"c":null}""", new TreeDiffOptions { NullEqualsMissing = true }).Should().BeEmpty();
	}

	[Test]
	public void MovedItemsAreMissingPlusUnexpected() =>
		Diff("""["a","b","c"]""", """["c","a","b"]""")
			.Select(d => (d.Kind, d.Path))
			.Should()
			.Equal((DifferenceKind.Unexpected, "$[0]"), (DifferenceKind.Missing, "$[2]"));

	[Test]
	public void LongStringsShowTheFirstDifferingLine() =>
		Lines("""{"t":"one\ntwo\nthree"}""", """{"t":"one\nTWO\nthree"}""").Should().Be("""$.t   line 2: expected "two"   actual "TWO"   (verified.json:1:6)""");

	[Test]
	public void InvalidVerifiedFileIsADifferenceNotACrash()
	{
		var comparison = JsonSnapshot.Compare("{not json", "{}");
		comparison.IsMatch.Should().BeFalse();
		comparison.Differences.Single().Single().Should().StartWith("The verified file is not valid JSON: ");
	}
}

public class JsonAssertionTests
{
	[Test]
	public void BeJsonPassesForEqualTrees()
	{
		var act = () => """{"b":[1,2.0],"a":{"x":null}}""".Should().BeJson("""{ "a": { "x": null }, "b": [1.0, 2] }""");
		act.Should().NotThrow();
	}

	[Test]
	public void BeJsonFailureMessage() =>
		Message(
			() => """{"items":[{"id":1,"name":"Kibana"}],"extra":true}""".Should().BeJson(
				"""{"items":[{"id":1,"name":"kibana"},{"id":2,"name":"beats"}]}""",
				because: "the catalog is stable"
			)
		).Should().MatchSnapshot();

	[Test]
	public void BeJsonOnInvalidJson() => Message(() => "{".Should().BeJson("{}")).Should().StartWith("Expected valid JSON, but: ");

	[Test]
	public void SnapshotOfAnObjectThroughSourceGeneratedMetadata()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(
			p,
			() => new Product(2, "Kibana", ["ui"])
				.Should()
				.MatchJsonSnapshot(TestJsonContext.Default.Product, callerFilePath: p.TestFile, callerMemberName: "RendersTable")
		);
		message.Should().StartWith("JSON snapshot is new");
		p
			.Read(p.ReceivedFile("Rendering/HtmlTests/RendersTable", "json"))
			.Should()
			.Be("{\n  \"Id\": 2,\n  \"Name\": \"Kibana\",\n  \"Tags\": [\n    \"ui\"\n  ]\n}\n");
	}

	[Test]
	public void SnapshotMismatchMessage()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		p.Fs.AddFile(
			p.VerifiedFile("Rendering/HtmlTests/RendersTable", "json"),
			new(JsonSnapshot.Pretty("""{"Id":2,"Name":"kibana","Tags":["ui","search"],"Owner":{"Team":"platform"}}"""))
		);
		var message = WithEngine(
			p,
			() => new Product(2, "Kibana", ["ui"])
				.Should()
				.MatchJsonSnapshot(TestJsonContext.Default.Product, callerFilePath: p.TestFile, callerMemberName: "RendersTable")
		);
		message.Should().MatchSnapshot();
	}

	[Test]
	public void WhitespaceAndKeyOrderDoNotFailAndDoNotRewriteVerified()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var verified = p.VerifiedFile("Rendering/HtmlTests/RendersTable", "json");
		p.Fs.AddFile(verified, new("{\n  \"Tags\": [\"ui\"], \"Name\": \"Kibana\", \"Id\": 2.0\n}\n"));
		var message = WithEngine(
			p,
			() => new Product(2, "Kibana", ["ui"])
				.Should()
				.MatchJsonSnapshot(TestJsonContext.Default.Product, callerFilePath: p.TestFile, callerMemberName: "RendersTable")
		);
		message.Should().BeNull();
		p.Read(verified).Should().Be("{\n  \"Tags\": [\"ui\"], \"Name\": \"Kibana\", \"Id\": 2.0\n}\n");
	}

	[Test]
	public void IgnorePathsReachTheSnapshotComparison()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		p.Fs.AddFile(p.VerifiedFile("Rendering/HtmlTests/RendersTable", "json"), new("""{"id":1,"at":"2026-01-01"}"""));
		var message = WithEngine(
			p,
			() => """{"id":1,"at":"2026-11-03"}""".Should().MatchJsonSnapshot(
				new TreeDiffOptions { IgnorePaths = ["$.at"] },
				callerFilePath: p.TestFile,
				callerMemberName: "RendersTable"
			)
		);
		message.Should().BeNull();
	}

	private static string? WithEngine(TestProject project, Action assertion)
	{
		Snapshot.EngineOverride.Value = project.Engine;
		try
		{
			assertion();
			return null;
		}
		catch (Exception e)
		{
			return e.Message;
		}
		finally
		{
			Snapshot.EngineOverride.Value = null;
		}
	}

	private static string Message(Action assertion) =>
		WithEngine(new TestProject(), assertion) ?? throw new InvalidOperationException("The assertion did not fail.");
}

public sealed record Product(int Id, string Name, string[] Tags);

[JsonSerializable(typeof(Product))]
public sealed partial class TestJsonContext : JsonSerializerContext;
