using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Json;
using Nullean.Snapshots.Json.Tests;
using Nullean.Snapshots.Tests;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Yaml.Tests;

public class YamlSnapshotTests
{
	private static readonly TreeDiffOptions Exact = new() { StrictNumbers = true, MapOrder = MapOrder.Significant };

	private static ScalarNode Scalar(string yaml) => (ScalarNode)((MapNode)YamlSnapshot.Parse("v: " + yaml)).Entries[0].Value;

	// The YAML 1.2 core schema, not 1.1: yes/no/on are strings, 1_000 and 0b101 are strings, 017 is decimal.
	[Test]
	[Arguments("yes", "String", "yes")]
	[Arguments("no", "String", "no")]
	[Arguments("on", "String", "on")]
	[Arguments("true", "Bool", "true")]
	[Arguments("True", "Bool", "true")]
	[Arguments("TRUE", "Bool", "true")]
	[Arguments("tRue", "String", "tRue")]
	[Arguments("false", "Bool", "false")]
	[Arguments("0x1F", "Int", "31")]
	[Arguments("0o17", "Int", "15")]
	[Arguments("017", "Int", "017")]
	[Arguments("+1", "Int", "+1")]
	[Arguments("-0", "Int", "-0")]
	[Arguments("1_000", "String", "1_000")]
	[Arguments("0b101", "String", "0b101")]
	[Arguments("0x1G", "String", "0x1G")]
	[Arguments("1e3", "Float", "1e3")]
	[Arguments("1.", "Float", "1.")]
	[Arguments(".5", "Float", ".5")]
	[Arguments(".inf", "Float", ".inf")]
	[Arguments("-.Inf", "Float", "-.Inf")]
	[Arguments(".NaN", "Float", ".NaN")]
	[Arguments(".nAn", "String", ".nAn")]
	[Arguments("~", "Null", "null")]
	[Arguments("null", "Null", "null")]
	[Arguments("Null", "Null", "null")]
	[Arguments("", "Null", "null")]
	[Arguments("12:30", "String", "12:30")]
	[Arguments("2026-11-03", "String", "2026-11-03")]
	[Arguments("\"true\"", "String", "true")]
	[Arguments("'1'", "String", "1")]
	[Arguments("!!str 1", "String", "1")]
	[Arguments("!!int \"5\"", "Int", "5")]
	[Arguments("!!float 1", "Float", "1")]
	[Arguments("!local yes", "String", "yes")]
	public void CoreSchemaTyping(string yaml, string kind, string canonical)
	{
		var scalar = Scalar(yaml);
		scalar.Kind.ToString().Should().Be(kind);
		scalar.Canonical.Should().Be(canonical);
	}

	[Test]
	public void HexAndDecimalCompareEqual() =>
		TreeDiffer.Instance.Diff(YamlSnapshot.Parse("a: 0x1F\nb: 0o17"), YamlSnapshot.Parse("a: 31\nb: 15.0")).Should().BeEmpty();

	[Test]
	public void AliasesAreTheAnchoredValue() =>
		TreeDiffer.Instance.Diff(
				YamlSnapshot.Parse("base: &b {x: 1, y: [2]}\ncopy: *b"),
				YamlSnapshot.Parse("base: {x: 1, y: [2]}\ncopy: {x: 1, y: [2]}"),
				Exact)
			.Should().BeEmpty();

	[Test]
	public void ExplicitKeysWinOverMergedOnesAndTheFirstMergeWins()
	{
		var yaml = """
			a: &a {x: 1, y: 1}
			b: &b {y: 2, z: 2}
			c:
			  <<: [*a, *b]
			  x: 9
			""";
		TreeDiffer.Instance.Diff(YamlSnapshot.Parse(yaml), YamlSnapshot.Parse("a: {x: 1, y: 1}\nb: {y: 2, z: 2}\nc: {y: 1, z: 2, x: 9}"))
			.Should().BeEmpty();
	}

	[Test]
	public void QuotedMergeKeyIsAnOrdinaryKey() =>
		((MapNode)YamlSnapshot.Parse("\"<<\": 1")).Entries.Single().Key.Canonical.Should().Be("<<");

	[Test]
	public void PositionsAreOneBased()
	{
		var map = (MapNode)YamlSnapshot.Parse("a: 1\nb:\n  - x\n  - y\n");
		map.Entries[0].Value.Span.Should().Be(new SourceSpan(1, 4));
		((SeqNode)map.Entries[1].Value).Items[1].Span.Should().Be(new SourceSpan(4, 5));
	}

	[Test]
	[Arguments("a: 1\na: 2", "appears twice")]
	[Arguments("a: *nowhere", "has no anchor")]
	[Arguments("a: &x 1\n---\nb: *x", "has no anchor")]
	[Arguments("? [1, 2]\n: x", "Only scalar keys")]
	[Arguments("<<: 1", "needs a mapping")]
	[Arguments("a: [1, 2", "While")]
	public void InvalidYamlIsAFormatException(string yaml, string message)
	{
		var act = () => YamlSnapshot.Parse(yaml);
		act.Should().Throw<FormatException>().Which.Message.Should().Contain(message);
	}

	[Test]
	public void EmptyAndMultiDocumentStreams()
	{
		YamlSnapshot.Parse("").Should().Be(ScalarNode.Null());
		YamlSnapshot.ParseStream("a: 1\n---\nb: 2\n").Documents.Should().Be(2);
		var lines = YamlSnapshot.Compare("a: 1\n---\nb: 2\n", "a: 1\n---\nb: 3\n").Differences.Select(d => d[0]);
		lines.Should().Equal("$doc[1].b   expected 2   actual 3   (verified.yml:3:4)");
	}

	// 10: JSON is YAML. Parsed by either package it is the same tree, kinds and number text included.
	[Test]
	public void JsonParsesTheSameAsYaml() =>
		TreeGen.Tree.Sample(tree =>
		{
			var json = TreeGen.ToJson(tree);
			return TreeDiffer.Instance.Diff(JsonSnapshot.Parse(json), YamlSnapshot.Parse(json), Exact).Count == 0;
		}, print: TreeGen.ToJson);

	// 11: a merge key and an alias read the same as writing the merged mapping out by hand.
	[Test]
	public void MergeKeysEqualTheirManualExpansion() =>
		Gen.Select(TreeGen.MapWith(1), TreeGen.MapWith(0)).Sample((baseMap, overrides) =>
		{
			var overrideKeys = overrides.Entries.Select(e => e.Key.Canonical).ToHashSet(StringComparer.Ordinal);
			var overrideJson = TreeGen.ToJson(overrides);
			var extra = overrides.Entries.Count == 0 ? "" : ", " + overrideJson[1..^1];
			var yaml = "{\"base\": &a " + TreeGen.ToJson(baseMap) + ", \"child\": {<<: *a" + extra + "}}";
			var expanded = new MapNode([
				new(ScalarNode.String("base"), baseMap),
				new(ScalarNode.String("child"), new MapNode([
					.. baseMap.Entries.Where(e => !overrideKeys.Contains(e.Key.Canonical)),
					.. overrides.Entries,
				])),
			]);
			return TreeDiffer.Instance.Diff(expanded, YamlSnapshot.Parse(yaml), new TreeDiffOptions { StrictNumbers = true }).Count == 0;
		}, print: t => TreeGen.ToJson(t.Item1) + " <- " + TreeGen.ToJson(t.Item2));
}

public class YamlAssertionTests
{
	private const string Key = "Rendering/HtmlTests/RendersTable";

	[Test]
	public void BeYamlComparesTreesNotText()
	{
		var act = () => "# a comment\nitems:\n  - {id: 1, tags: [a]}\n".Should().BeYaml("items: [{tags: [a], id: 1.0}]");
		act.Should().NotThrow();
	}

	[Test]
	public void SnapshotIsStoredVerbatim()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		const string yaml = "# kept\nname: kibana   # trailing comment\nlist:\n- a\n";
		var message = WithEngine(p, () => yaml.Should().MatchYamlSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable"));
		message.Should().StartWith("YAML snapshot is new");
		p.Read(p.ReceivedFile(Key, "yml")).Should().Be(yaml);
	}

	[Test]
	public void SnapshotMismatchMessage()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		p.Fs.AddFile(p.VerifiedFile(Key, "yml"), new("""
			# products
			items:
			  - name: kibana
			    tier: 1
			  - name: beats
			    tier: 2
			meta:
			  version: 0x10
			"""));
		var message = WithEngine(p, () => """
			items:
			  - name: Kibana
			    tier: 1
			meta:
			  version: 16
			  extra: true
			""".Should().MatchYamlSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable"));
		message.Should().MatchSnapshot();
	}

	[Test]
	public void InvalidYamlFailsWithoutWritingAnything()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(p, () => "a: [1".Should().MatchYamlSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable"));
		message.Should().StartWith("Expected valid YAML, but: ");
		p.Fs.File.Exists(p.ReceivedFile(Key, "yml")).Should().BeFalse();
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
}
