using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using AwesomeAssertions;

namespace Nullean.Snapshots.Cli.Tests;

/// <summary>The CLI on inline snapshots: received records that edit a literal in a test's source.</summary>
public sealed class SnapCliInlineTests : IDisposable
{
	private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

	private const string Original =
		"class HtmlTests\n{\n    void A() => x.Should().MatchInlineSnapshot(\"one\");\n    void B() => y.Should().MatchInlineSnapshot(\"two\");\n}\n";

	private readonly MockFileSystem _fs = new();
	private readonly StringWriter _out = new();
	private readonly StringWriter _err = new();

	public SnapCliInlineTests()
	{
		_fs.AddDirectory(P(".git"));
		_fs.AddFile(P("tests", "Acme.Tests", "Acme.Tests.csproj"), new("<Project />"));
		Source(Original);
		_fs.AddFile(_fs.Path.Combine(Received(), ReceivedMarker.FileName), new("project=Acme.Tests\nverified=../../../../tests/Acme.Tests/.snapshots\n"));
		Record("A.L3", line: 3, expression: "\"one\"", expected: "one", value: "1\n2\n");
		Record("B.L4", line: 4, expression: "\"two\"", expected: "two", value: "2\n");
	}

	public void Dispose()
	{
		_out.Dispose();
		_err.Dispose();
	}

	private string P(params string[] parts) => _fs.Path.Combine([Root, .. parts]);

	private string Received() => P(".artifacts", ".snapshots", "received", "Acme.Tests");

	private string SourcePath => P("tests", "Acme.Tests", "Rendering", "HtmlTests.cs");

	private string ReceivedFile(string name) => _fs.Path.Combine(Received(), "Rendering", "HtmlTests", name + ".received.inline");

	private void Source(string text) => _fs.AddFile(SourcePath, new MockFileData(text));

	private string Read() => _fs.File.ReadAllText(SourcePath);

	private void Record(string name, int line, string expression, string? expected, string value, string? hash = "from-source", string? sourceHash = null)
	{
		var json = InlineRecord.Render(
			"../../../../tests/Acme.Tests/Rendering/HtmlTests.cs",
			"Text",
			new InlineEdit("MatchInlineSnapshot", line, expression, expected, value),
			hash is null ? null : sourceHash ?? InlineSnapshotSource.Hash(_fs.File.ReadAllBytes(SourcePath)));
		_fs.AddFile(ReceivedFile(name), new MockFileData(json));
	}

	private SnapCli Cli() => new(_fs, Root, _out, _err);

	private string Out() => _out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

	[Test]
	public void StatusListsInlineSnapshotsWithTheirReceivedExtension()
	{
		Cli().Status(json: false).Should().Be(ExitCode.Pending);
		Out().Should().MatchSnapshot();
	}

	[Test]
	public void StatusJsonNamesTheSourceLocationAndHasNoVerifiedFile()
	{
		Cli().Status(json: true).Should().Be(ExitCode.Pending);
		using var document = JsonDocument.Parse(_out.ToString());
		var first = document.RootElement[0];
		first.GetProperty("key").GetString().Should().Be("Acme.Tests/Rendering/HtmlTests/A.L3");
		first.GetProperty("ext").GetString().Should().Be("inline");
		first.GetProperty("state").GetString().Should().Be("changed");
		first.GetProperty("verified").ValueKind.Should().Be(JsonValueKind.Null);
		first.GetProperty("source").GetString().Should().Be("tests/Acme.Tests/Rendering/HtmlTests.cs:3");
	}

	[Test]
	public void DiffShowsTheLiteralAgainstTheReceivedValue()
	{
		Cli().Diff("A.L3").Should().Be(ExitCode.Ok);
		Out().Should().MatchSnapshot();
	}

	[Test]
	public void DiffOfANewInlineSnapshotSaysThereIsNoExpectedValue()
	{
		Source("class HtmlTests\n{\n    void A() => x.Should().MatchInlineSnapshot();\n}\n");
		Record("A.L3", line: 3, expression: null!, expected: null, value: "fresh\n");

		Cli().Diff("A.L3").Should().Be(ExitCode.Ok);
		Out().Should().Contain("inline, new: no expected value").And.Contain("+fresh");
		Cli().Status(json: false);
		Out().Should().Contain("new ");
	}

	[Test]
	public void AcceptWritesTheValueIntoTheLiteralAndRemovesTheRecord()
	{
		Cli().Accept(["B.L4"], all: false).Should().Be(ExitCode.Ok);

		Read().Should().Be("class HtmlTests\n{\n    void A() => x.Should().MatchInlineSnapshot(\"one\");\n    void B() => y.Should().MatchInlineSnapshot(\"2\");\n}\n");
		_fs.File.Exists(ReceivedFile("B.L4")).Should().BeFalse();
		_fs.File.Exists(ReceivedFile("A.L3")).Should().BeTrue();
		Out().Should().Be("accepted  Acme.Tests/Rendering/HtmlTests/B.L4  -> tests/Acme.Tests/Rendering/HtmlTests.cs:4\n");
	}

	[Test]
	public void AcceptingTwoInOneFileIsOneEditEvenThoughTheFirstGrowsTheFile()
	{
		Cli().Accept([], all: true).Should().Be(ExitCode.Ok);

		Read().Should().Be(
			"class HtmlTests\n{\n    void A() => x.Should().MatchInlineSnapshot(\"\"\"\n        1\n        2\n        \"\"\");\n    void B() => y.Should().MatchInlineSnapshot(\"2\");\n}\n");
		_fs.AllFiles.Should().NotContain(f => f.EndsWith(".received.inline", StringComparison.Ordinal));
	}

	[Test]
	public void AcceptRefusesAFileThatChangedSinceTheTestRanAndKeepsTheRecords()
	{
		var edited = Original.Replace("class HtmlTests", "// edited\nclass HtmlTests", StringComparison.Ordinal);
		Source(edited);

		Cli().Accept([], all: true).Should().Be(ExitCode.Pending);

		Read().Should().Be(edited);
		_fs.File.Exists(ReceivedFile("A.L3")).Should().BeTrue();
		_err.ToString().Should().Contain("the file changed since the test ran").And.Contain("HtmlTests.cs:3");
	}

	[Test]
	public void AcceptAppliesWhatItCanAndRefusesTheRest()
	{
		Record("B.L4", line: 4, expression: "\"two\"", expected: "stale", value: "2\n");

		Cli().Accept([], all: true).Should().Be(ExitCode.Pending);

		Read().Should().Contain("MatchInlineSnapshot(\"\"\"").And.Contain("MatchInlineSnapshot(\"two\")");
		_fs.File.Exists(ReceivedFile("A.L3")).Should().BeFalse();
		_fs.File.Exists(ReceivedFile("B.L4")).Should().BeTrue();
		_err.ToString().Should().Contain("no longer holds the value");
	}

	[Test]
	public void AcceptWorksWithoutAHashWhenTheTestCouldNotReadTheSource()
	{
		Record("A.L3", line: 3, expression: "\"one\"", expected: "one", value: "x\n", hash: null);
		Cli().Accept(["A.L3"], all: false).Should().Be(ExitCode.Ok);
		Read().Should().Contain("MatchInlineSnapshot(\"x\")");
	}

	[Test]
	public void AcceptRefusesAnExpectedValueThatIsNotALiteral()
	{
		Source("class HtmlTests\n{\n    void A() => x.Should().MatchInlineSnapshot(Expected);\n    void B() => y.Should().MatchInlineSnapshot(\"two\");\n}\n");
		Record("A.L3", line: 3, expression: "Expected", expected: "one", value: "1\n");
		Record("B.L4", line: 4, expression: "\"two\"", expected: "two", value: "2\n");

		Cli().Accept([], all: true).Should().Be(ExitCode.Pending);

		_err.ToString().Should().Contain("not a plain string literal");
		Read().Should().Contain("MatchInlineSnapshot(Expected)").And.Contain("MatchInlineSnapshot(\"2\")");
	}

	[Test]
	public void AcceptKeepsALfFileLfAndACrlfFileCrlf()
	{
		Source(Original.Replace("\n", "\r\n", StringComparison.Ordinal));
		Record("A.L3", line: 3, expression: "\"one\"", expected: "one", value: "1\n2\n");
		Record("B.L4", line: 4, expression: "\"two\"", expected: "two", value: "2\n");

		Cli().Accept([], all: true).Should().Be(ExitCode.Ok);

		var text = Read();
		text.Should().Contain("\r\n");
		text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Should().NotContain("\n");
	}

	[Test]
	public void RejectDeletesTheRecordAndLeavesTheSourceAlone()
	{
		Cli().Reject(["A.L3"], all: false).Should().Be(ExitCode.Ok);
		_fs.File.Exists(ReceivedFile("A.L3")).Should().BeFalse();
		Read().Should().Be(Original);
	}

	[Test]
	public void ReviewAppliesAcceptedInlineSnapshotsTogetherAtTheEnd()
	{
		Cli().Review(new Scripted('a', 'a'), color: false).Should().Be(ExitCode.Ok);

		Read().Should().Contain("\"\"\"").And.Contain("MatchInlineSnapshot(\"2\")");
		Out().Should().Contain("2 accepted, 0 rejected, 0 still pending.");
	}

	[Test]
	public void ReviewCountsARefusedInlineAcceptAsStillPending()
	{
		Source(Original.Replace("// ", string.Empty, StringComparison.Ordinal) + "// changed after the run\n");

		Cli().Review(new Scripted('a', 's'), color: false).Should().Be(ExitCode.Pending);

		Out().Should().Contain("0 accepted, 0 rejected, 2 still pending.");
		_fs.File.Exists(ReceivedFile("A.L3")).Should().BeTrue();
	}

	[Test]
	public void ATestFileOutsideTheRootIsSkippedWithAWarningAndNeverTouched()
	{
		var outside = _fs.Path.Combine(_fs.Path.GetPathRoot(Root)!, "elsewhere", "Linked.cs");
		_fs.AddFile(outside, new("x.MatchInlineSnapshot(\"a\");"));
		var record = InlineRecord.Render("../../../../../elsewhere/Linked.cs", "Text", new InlineEdit("MatchInlineSnapshot", 1, "\"a\"", "a", "b\n"), null);
		_fs.AddFile(ReceivedFile("C.L1"), new MockFileData(record));

		Cli().Status(json: false).Should().Be(ExitCode.Pending);
		Cli().Accept([], all: true).Should().Be(ExitCode.Ok);

		_out.ToString().Should().NotContain("C.L1");
		_err.ToString().Should().Contain("Skipping Acme.Tests/Rendering/HtmlTests/C.L1").And.Contain("is outside").And.Contain("Pass --root");
		_fs.File.ReadAllText(outside).Should().Be("x.MatchInlineSnapshot(\"a\");");
		_fs.File.Exists(ReceivedFile("C.L1")).Should().BeTrue();
	}

	[Test]
	public void ARecordThatIsNotJsonIsSkippedWithAWarningAndTheRestStillWork()
	{
		_fs.AddFile(ReceivedFile("Broken.L9"), new("not json"));

		Cli().Status(json: false).Should().Be(ExitCode.Pending);

		_out.ToString().Should().NotContain("Broken");
		_err.ToString().Should().Contain("Skipping").And.Contain("Broken.L9.received.inline").And.Contain("not an inline snapshot record");
	}

	[Test]
	public void ARecordPointingAtANonCSharpFileIsRefused()
	{
		var other = P("tests", "Acme.Tests", "notes.txt");
		_fs.AddFile(other, new("MatchInlineSnapshot(\"one\")"));
		var record = InlineRecord.Render("../../../../tests/Acme.Tests/notes.txt", "Text", new InlineEdit("MatchInlineSnapshot", 1, "\"one\"", "one", "x\n"), null);
		_fs.AddFile(ReceivedFile("N.L1"), new MockFileData(record));

		Cli().Accept(["N.L1"], all: false).Should().Be(ExitCode.Pending);

		_fs.File.ReadAllText(other).Should().Be("MatchInlineSnapshot(\"one\")");
		_err.ToString().Should().Contain("not a C# file");
	}

	private sealed class Scripted(params char[] answers) : ISnapPrompt
	{
		private int _asked;

		public char? Ask(string question) => _asked < answers.Length ? answers[_asked++] : null;
	}
}
