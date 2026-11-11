using AwesomeAssertions;

namespace Nullean.Snapshots.Tests;

/// <summary>The inline snapshot workflow on a mock project: records, messages, and accepting into the source.</summary>
public class InlineSnapshotEngineTests
{
	private const string Key = "Rendering/HtmlTests/RendersTable.L10";

	/// <summary>A test file whose line 10 holds the assertion.</summary>
	private static string Source(string call = "x.Should().MatchInlineSnapshot(\"old\");") => string.Concat(Enumerable.Repeat("//\n", 9)) + call + "\n";

	private static InlineRecord Record(string text) => InlineRecord.Parse(text);

	[Test]
	public void ANewSnapshotFailsAndWritesARecord()
	{
		var p = new TestProject();
		var result = p.MatchInline("first {value}\r\n", expected: null);

		result.Passed.Should().BeFalse();
		result.Key.Should().Be("Acme.Tests/" + Key);
		var record = Record(p.Read(p.ReceivedFile(Key, "inline")));
		record.Edit.Expected.Should().BeNull();
		record.Edit.NewValue.Should().Be("first {value}\n");
		record.Edit.Line.Should().Be(10);
		record.Edit.MethodName.Should().Be("MatchInlineSnapshot");
		record.Format.Should().Be("Text");
		p
			.Fs
			.Path
			.GetFullPath(p.Fs.Path.Combine(p.Project.ReceivedDirectory, record.Source.Replace('/', p.Fs.Path.DirectorySeparatorChar)))
			.Should()
			.Be(p.TestFile);
		result.FailureMessage.Should().MatchSnapshot();
	}

	[Test]
	public void AMismatchShowsTheDifferencesAndWritesBothValues()
	{
		var p = new TestProject();
		var result = p.MatchInline("a\nB\nc\n", "a\nb\nc", "\"a\\nb\\nc\"");

		result.Passed.Should().BeFalse();
		var record = Record(p.Read(p.ReceivedFile(Key, "inline")));
		record.Edit.Expected.Should().Be("a\nb\nc");
		record.Edit.Expression.Should().Be("\"a\\nb\\nc\"");
		record.Edit.NewValue.Should().Be("a\nB\nc\n");
		result.FailureMessage.Should().MatchSnapshot();
	}

	[Test]
	public void AMatchPassesAndRemovesAStaleRecord()
	{
		var p = new TestProject();
		p.Fs.AddFile(p.ReceivedFile(Key, "inline"), new("old record"));

		// The literal is compared normalised: line endings and the trailing newline do not matter.
		var result = p.MatchInline("a\nb\n", "a\r\nb", "\"a\\r\\nb\"");

		result.Passed.Should().BeTrue();
		p.Fs.File.Exists(p.ReceivedFile(Key, "inline")).Should().BeFalse();
	}

	[Test]
	public void ScrubbersApplyToTheActualValueOnly()
	{
		var p = new TestProject();
		var options = new SnapshotOptions().Scrub(s => s.Replace("secret", "***", StringComparison.Ordinal));

		p.MatchInline("token: secret", "token: ***", "\"token: ***\"", options: options).Passed.Should().BeTrue();
		p.MatchInline("token: secret", "token: secret", "\"token: secret\"", options: options, line: 11).Passed.Should().BeFalse();
	}

	[Test]
	public void AnEmptyExpectedValueIsNotANewSnapshot()
	{
		var p = new TestProject();
		p.MatchInline("x", "", "\"\"").FailureMessage.Should().StartWith("Text did not match inline snapshot: tests/Acme.Tests/Rendering/HtmlTests.cs:10");
		p.MatchInline("", "", "\"\"", line: 11).Passed.Should().BeTrue();
	}

	[Test]
	public void InlineSnapshotsWriteNoVerifiedFileAndLeaveAManifestWithoutEntries()
	{
		var p = new TestProject();
		p.MatchInline("a", "a", "\"a\"").Passed.Should().BeTrue();
		p.Engine.FlushManifests();

		p.Fs.AllFiles.Should().NotContain(f => f.Contains(".verified.", StringComparison.Ordinal));
		var manifest = p.Read(p.Fs.Path.Combine(p.Project.ReceivedDirectory, RunManifest.FileName));
		RunManifest.Read(manifest).Files.Should().BeEmpty();
	}

	[Test]
	public void DataDrivenCasesGetOneRecordEach()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike("HtmlTests", "RendersTable", 1) };
		p.MatchInline("one", "x", "\"x\"");
		p.Identity = TestProject.TUnitLike("HtmlTests", "RendersTable", 2);
		p.MatchInline("two", "x", "\"x\"");

		p.Fs.AllFiles.Count(f => f.EndsWith(".received.inline", StringComparison.Ordinal)).Should().Be(2);
	}

	[Test]
	public void TwoDifferentCallsOnOneLineOfOneTestNeedNoName()
	{
		var p = new TestProject();
		p.MatchInline("a", "a", "\"a\"").Passed.Should().BeTrue();
		p.MatchInline("b", "b", "\"b\"").Passed.Should().BeTrue("the same call site claiming again, as a retried test does, is fine");
	}

	[Test]
	public void AcceptingEditsTheSourceWhenTheProcessEnds()
	{
		var p = new TestProject();
		p.AddSource(Source());
		p.Environment["SNAPSHOT_ACCEPT"] = "1";

		var result = p.MatchInline("new\nvalue\n", "old", "\"old\"");

		result.Passed.Should().BeTrue();
		p.Fs.File.Exists(p.ReceivedFile(Key, "inline")).Should().BeFalse();
		p.Read(p.TestFile).Should().Be(Source(), "tests run on; the source is edited once, when the process ends");

		p.Engine.FlushManifests();

		p
			.Read(p.TestFile)
			.Should()
			.Be(string.Concat(Enumerable.Repeat("//\n", 9)) + "x.Should().MatchInlineSnapshot(\"\"\"\n    new\n    value\n    \"\"\");\n");
	}

	[Test]
	public void AcceptingKeepsTheFilesByteOrderMark()
	{
		var p = new TestProject();
		p.AddSource(Source(), bom: true);
		p.Environment["SNAPSHOT_ACCEPT"] = "1";
		p.MatchInline("new", "old", "\"old\"").Passed.Should().BeTrue();
		p.Engine.FlushManifests();

		p.Fs.File.ReadAllBytes(p.TestFile).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
		p.Fs.File.ReadAllText(p.TestFile).Should().Contain("MatchInlineSnapshot(\"new\")");
	}

	[Test]
	public void AcceptingFillsInANewSnapshot()
	{
		var p = new TestProject();
		p.AddSource(Source("x.Should().MatchInlineSnapshot();"));
		p.Environment["SNAPSHOT_ACCEPT"] = "1";
		p.MatchInline("fresh", null).Passed.Should().BeTrue();
		p.Engine.FlushManifests();

		p.Read(p.TestFile).Should().Contain("MatchInlineSnapshot(\"fresh\")");
	}

	[Test]
	public void AcceptingNeverHappensInCi()
	{
		var p = new TestProject();
		p.AddSource(Source());
		p.Environment["SNAPSHOT_ACCEPT"] = "1";
		p.Environment["CI"] = "true";

		var result = p.MatchInline("new", "old", "\"old\"");
		p.Engine.FlushManifests();

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("is ignored because CI is set");
		p.Read(p.TestFile).Should().Be(Source());
		p.Fs.File.Exists(p.ReceivedFile(Key, "inline")).Should().BeTrue();
	}

	[Test]
	public void AnExpectedValueThatIsNotALiteralFailsWithTheReasonAndStillWritesTheRecord()
	{
		var p = new TestProject();
		p.AddSource(Source("x.Should().MatchInlineSnapshot(Expected);"));
		p.Environment["SNAPSHOT_ACCEPT"] = "1";

		var result = p.MatchInline("new", "old", "Expected");

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().MatchSnapshot();
		p.Fs.File.Exists(p.ReceivedFile(Key, "inline")).Should().BeTrue();
		p.Read(p.TestFile).Should().Be(Source("x.Should().MatchInlineSnapshot(Expected);"));
	}

	[Test]
	public void ASourceThatChangedSinceTheBuildIsNotAccepted()
	{
		var p = new TestProject();
		p.AddSource(Source("x.Should().MatchInlineSnapshot(\"edited\");"));
		p.Environment["SNAPSHOT_ACCEPT"] = "1";

		var result = p.MatchInline("new", "old", "\"old\"");

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("Not accepted:");
	}

	[Test]
	public void CasesOfOneTestThatDisagreeAboutOneLiteralDoNotBothPass()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike("HtmlTests", "RendersTable", 1) };
		p.AddSource(Source());
		p.Environment["SNAPSHOT_ACCEPT"] = "1";

		p.MatchInline("one", "old", "\"old\"").Passed.Should().BeTrue();
		p.MatchInline("one", "old", "\"old\"").Passed.Should().BeTrue("the same value again is fine");
		p.Identity = TestProject.TUnitLike("HtmlTests", "RendersTable", 2);
		var second = p.MatchInline("two", "old", "\"old\"");

		second.Passed.Should().BeFalse();
		second.FailureMessage.Should().Contain("use MatchSnapshot for values that differ per case");
	}

	[Test]
	public void ASourceOutsideTheProjectIsNeverEdited()
	{
		var p = new TestProject();
		var outside = p.Fs.Path.Combine(TestProject.Root, "elsewhere", "Linked.cs");
		p.Fs.AddFile(outside, new(Source()));
		p.Environment["SNAPSHOT_ACCEPT"] = "1";

		var result = p.Engine.MatchInline(
			"new",
			"old",
			SnapshotFormats.Text,
			new InlineSnapshotCallSite(new SnapshotCallSite(outside, "RendersTable", 10), "MatchInlineSnapshot", "\"old\""),
			null,
			null
		);
		p.Engine.FlushManifests();

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("outside the test project");
		p.Read(outside).Should().Be(Source());
	}

	[Test]
	public void ADeterministicBuildPathIsMappedBackIntoTheProject()
	{
		var p = new TestProject();
		p.AddSource(Source());
		p.Environment["SNAPSHOT_ACCEPT"] = "1";

		var result = p.Engine.MatchInline(
			"new",
			"old",
			SnapshotFormats.Text,
			new InlineSnapshotCallSite(
				new SnapshotCallSite("/_/tests/Acme.Tests/Rendering/HtmlTests.cs", "RendersTable", 10),
				"MatchInlineSnapshot",
				"\"old\""
			),
			null,
			null
		);
		p.Engine.FlushManifests();

		result.Passed.Should().BeTrue();
		p.Read(p.TestFile).Should().Contain("MatchInlineSnapshot(\"new\")");
	}
}
