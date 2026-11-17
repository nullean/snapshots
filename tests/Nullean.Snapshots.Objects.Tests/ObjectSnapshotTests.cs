using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Trees;
using Nullean.Snapshots.Yaml;

namespace Nullean.Snapshots.Objects.Tests;

[SuppressMessage("Trimming", "IL2026", Justification = "The tests exercise the reflection API.")]
public class ObjectSnapshotTests
{
	private enum Color
	{
		Red,
		Green,
	}

	private sealed record Docset(string Name, Color Color, Uri Url, DateTimeOffset At, int[] Sizes, Dictionary<string, int> Counts, Docset? Child);

	private sealed record Links(HashSet<string> Set, List<string> List);

	private sealed class Self
	{
		public string Name { get; set; } = "me";
		public Self? Me { get; set; }
		public IReadOnlyList<Self> Others { get; set; } = [];
	}

	private sealed class Throws
	{
		public int Fine => 1;
		public int Bad => throw new InvalidOperationException("message that differs per OS");
	}

	private sealed class Holder
	{
		public required string Keep { get; init; }
		public string Secret { get; init; } = "s";
		public required IDisposable Handle { get; init; }
		public required Guid Id { get; init; }
	}

	private sealed class NoOp : IDisposable
	{
		public void Dispose()
		{ }
	}

	[Test]
	public void ARecordGraphIsWrittenAsYaml()
	{
		var value = new Docset(
			"docs",
			Color.Green,
			new Uri("https://example.com/a"),
			new DateTimeOffset(2026, 11, 3, 8, 53, 10, TimeSpan.Zero),
			[3, 1],
			new Dictionary<string, int> { ["b"] = 2, ["a"] = 1 },
			new Docset("child", Color.Red, new Uri("https://example.com/b"), DateTimeOffset.UnixEpoch, [], [], null)
		);

		ObjectSnapshot
			.Dump(value)
			.Should()
			.Be(
				"""
			Name: "docs"
			Color: "Green"
			Url: "https://example.com/a"
			At: "2026-11-03T08:53:10.0000000+00:00"
			Sizes:
			  - 3
			  - 1
			Counts:
			  a: 1
			  b: 2
			Child:
			  Name: "child"
			  Color: "Red"
			  Url: "https://example.com/b"
			  At: "1970-01-01T00:00:00.0000000+00:00"
			  Sizes: []
			  Counts: {}
			  Child: null

			"""
			);
	}

	[Test]
	public void ASequenceOfMapsPutsTheFirstEntryOnTheDashLine() =>
		ObjectSnapshot
			.Dump(new[] { new Links(["x"], ["y"]), new Links([], []) })
			.Should()
			.Be("""
			- Set:
			    - "x"
			  List:
			    - "y"
			- Set: []
			  List: []

			""");

	[Test]
	public void ASetIsSortedAndAListKeepsItsOrder() =>
		ObjectSnapshot
			.Dump(new Links(["b", "c", "a"], ["b", "c", "a"]))
			.Should()
			.Be(
				"""
			Set:
			  - "a"
			  - "b"
			  - "c"
			List:
			  - "b"
			  - "c"
			  - "a"

			"""
			);

	[Test]
	public void ASetDumpsTheSameInAnyInsertionOrder() =>
		Gen
			.String[Gen.Char["abc"], 0, 3]
			.List[0, 12]
			.Sample(items =>
			{
				var forward = ObjectSnapshot.Dump(new HashSet<string>(items));
				var backward = ObjectSnapshot.Dump(new HashSet<string>(Enumerable.Reverse(items)));
				return forward == backward;
			});

	[Test]
	public void AReferenceBackToTheRootIsACycleMarker()
	{
		var self = new Self();
		self.Me = self;
		self.Others = [self];

		ObjectSnapshot
			.Dump(self)
			.Should()
			.Be("""
			Name: "me"
			Me: "<cycle to $>"
			Others:
			  - "<cycle to $>"

			""");
	}

	private sealed class Empty;

	private sealed record Wraps(Empty Inner);

	private sealed class MySet<T> : List<T>;

	[Test]
	public void ATypeWithoutMembersFailsWhenIncludesAreRequiredUnlessItWasNamed()
	{
		// What a native AOT run does by default: the trimmer removes the members of a type nobody named.
		var strict = new ObjectDumpOptions { RequireIncludedTypes = true };
		var act = () => ObjectSnapshot.Dump(new Wraps(new Empty()), strict);
		act.Should().Throw<InvalidOperationException>().WithMessage("*Include<Empty>()*");

		ObjectSnapshot.Dump(new Wraps(new Empty()), new ObjectDumpOptions { RequireIncludedTypes = true }.Include<Empty>()).Should().Be("Inner: {}\n");
	}

	[Test]
	public void SortAsSetSortsACustomCollectionAndOtherwiseItKeepsItsOrder()
	{
		var items = new MySet<string> { "b", "a" };

		ObjectSnapshot.Dump(items).Should().Be("- \"b\"\n- \"a\"\n");
		ObjectSnapshot.Dump(items, new ObjectDumpOptions().SortAsSet(typeof(MySet<>))).Should().Be("- \"a\"\n- \"b\"\n");
	}

	[Test]
	public void ASharedButNotCyclicReferenceIsWrittenInFull()
	{
		var shared = new Self { Name = "shared" };
		var root = new Self { Others = [shared, shared] };

		ObjectSnapshot.Dump(root).Should().NotContain("<cycle").And.Contain("shared");
	}

	[Test]
	public void APropertyThatThrowsIsNamedByItsTypeOnly() =>
		ObjectSnapshot.Dump(new Throws()).Should().Be("""
			Fine: 1
			Bad: "<throws InvalidOperationException>"

			""");

	[Test]
	public void IgnoredMembersAndTypesAreLeftOutAndRenderersReplaceValues() =>
		ObjectSnapshot
			.Dump(
				new Holder { Keep = "k", Handle = new NoOp(), Id = Guid.Empty },
				new ObjectDumpOptions().IgnoreMember("Secret").IgnoreType<IDisposable>().Render<Guid>(_ => "<guid>")
			)
			.Should()
			.Be("""
			Keep: "k"
			Id: "<guid>"

			""");

	[Test]
	public void DeepGraphsStopAtMaxDepth() =>
		ObjectSnapshot.Dump(new Self { Me = new Self { Me = new Self() } }, new ObjectDumpOptions { MaxDepth = 2 }).Should().Contain("\"<max depth>\"");

	[Test]
	public void LongCollectionsAreCappedAtMaxItems() =>
		ObjectSnapshot.Dump(new[] { 1, 2, 3 }, new ObjectDumpOptions { MaxItems = 2 }).Should().Be("- 1\n- 2\n- \"<+more>\"\n");

	[Test]
	public void AnyStringRoundTripsThroughTheYamlParser() =>
		Gen.String.Sample(text =>
		{
			var yaml = ObjectSnapshot.Dump(new Links([text], [text]));
			var map = (MapNode)YamlSnapshot.Parse(yaml);
			var set = (SeqNode)map.Entries.Single(e => e.Key.Canonical == "Set").Value;
			return ((ScalarNode)set.Items.Single()).Canonical == text;
		});

	[Test]
	public void ANewPropertyIsReportedAsAnAddedPath()
	{
		var before = ObjectSnapshot.Dump(new { Name = "a" });
		var after = ObjectSnapshot.Dump(new { Name = "a", Added = 1 });

		var comparison = YamlSnapshot.Compare(before, after);

		comparison.IsMatch.Should().BeFalse();
		string.Join('\n', comparison.Differences.SelectMany(d => d)).Should().Contain("$.Added");
	}

	[Test]
	public void AMatchingObjectPassesAgainstItsVerifiedFile() =>
		new Docset("docs", Color.Red, new Uri("https://example.com/"), DateTimeOffset.UnixEpoch, [1], [], null).Should().MatchObjectSnapshot();
}
