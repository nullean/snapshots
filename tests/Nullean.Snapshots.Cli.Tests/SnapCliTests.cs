using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using AwesomeAssertions;
using Nullean.Snapshots.Cli;

namespace Nullean.Snapshots.Cli.Tests;

public class SnapCliTests : IDisposable
{
	private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

	private readonly MockFileSystem _fs = new();
	private readonly StringWriter _out = new();
	private readonly StringWriter _err = new();

	public void Dispose()
	{
		_out.Dispose();
		_err.Dispose();
		GC.SuppressFinalize(this);
	}

	private string P(params string[] parts) => _fs.Path.Combine([Root, .. parts]);

	private const string Changed = "Acme.Tests/Rendering/HtmlTests/RendersTable";
	private const string New = "Acme.Tests/Rendering/HtmlTests/RendersList.a1b2c3d4";
	private const string Obj = "Lib/X/Y";

	public SnapCliTests()
	{
		_fs.AddDirectory(P(".git"));
		_fs.AddFile(P("tests", "Acme.Tests", "Acme.Tests.csproj"), new("<Project />"));
		_fs.AddFile(P("src", "Lib", "Lib.csproj"), new("<Project />"));
		_fs.AddFile(P("tests", "Acme.Tests", ".snapshots", "Rendering", "HtmlTests", "RendersTable.verified.txt"), new("a\nb\nc\n"));
		var received = P(".artifacts", ".snapshots", "received", "Acme.Tests");
		_fs.AddFile(_fs.Path.Combine(received, ReceivedMarker.FileName), new("project=Acme.Tests\nverified=../../../../tests/Acme.Tests/.snapshots\n"));
		_fs.AddFile(_fs.Path.Combine(received, "Rendering", "HtmlTests", "RendersTable.received.txt"), new("a\nB\nc\n"));
		_fs.AddFile(_fs.Path.Combine(received, "Rendering", "HtmlTests", "RendersList.a1b2c3d4.received.txt"), new("new {x}\r\n"));
		_fs.AddFile(_fs.Path.Combine(received, "Rendering", "HtmlTests", "Leftover.received.txt.0a1b.tmp"), new("half"));
		_fs.AddFile(P("src", "Lib", "obj", "snapshots", "received", "X", "Y.received.txt"), new("obj\n"));
		// Folders discovery must not walk into.
		_fs.AddFile(P("node_modules", "pkg", "pkg.csproj"), new("<Project />"));
		_fs.AddFile(P("node_modules", "pkg", "obj", "snapshots", "received", "Z.received.txt"), new("no"));
		_fs.AddFile(P(".github", "Hidden.csproj"), new("<Project />"));
	}

	private SnapCli Cli(string? root = null) => new(_fs, root ?? Root, _out, _err);

	private string Verified(params string[] parts) => P(["tests", "Acme.Tests", ".snapshots", .. parts]);

	private string Received(params string[] parts) => P([".artifacts", ".snapshots", "received", "Acme.Tests", .. parts]);

	[Test]
	public void SiteComparisonFilesAreNeverPending()
	{
		var received = P(".artifacts", ".snapshots", "received", "Acme.Tests", "journeys");
		_fs.AddFile(_fs.Path.Combine(received, "shop.baseline.journey"), new("## get title\n> Shop\n"));
		_fs.AddFile(_fs.Path.Combine(received, "shop.target.journey"), new("## get title\n> Store\n"));
		_fs.AddFile(_fs.Path.Combine(received, "shop.baseline.responses"), new("## GET /\nstatus: 200 OK\n"));

		var exit = Cli().Status(json: false);

		exit.Should().Be(1, "the other pending snapshots are still pending");
		_out.ToString().Should().NotContain("shop").And.NotContain("journeys");
		Cli().Reject([], all: true).Should().Be(0);
		_fs.FileExists(_fs.Path.Combine(received, "shop.baseline.journey")).Should().BeTrue("snap reject leaves a baseline alone");
	}

	[Test]
	public void StatusListsPendingAndExitsOne()
	{
		Cli().Status(json: false).Should().Be(ExitCode.Pending);
		_out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Should().MatchSnapshot();
		_err.ToString().Should().BeEmpty();
	}

	[Test]
	public void StatusJson()
	{
		Cli().Status(json: true).Should().Be(ExitCode.Pending);
		var json = _out.ToString();
		using var document = JsonDocument.Parse(json);
		document.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString()).Should().Equal(New, Changed, Obj);
		json.Replace("\r\n", "\n", StringComparison.Ordinal).Should().MatchSnapshot();
	}

	[Test]
	public void StatusWithNothingPendingExitsZero()
	{
		var empty = new MockFileSystem();
		empty.AddDirectory(Root);
		new SnapCli(empty, Root, _out, _err).Status(json: false).Should().Be(ExitCode.Ok);
		_out.ToString().Should().Be("No pending snapshots." + Environment.NewLine);
	}

	[Test]
	public void DiffShowsHunks()
	{
		Cli().Diff("RendersTable").Should().Be(ExitCode.Ok);
		_out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Should().Be(
			"--- tests/Acme.Tests/.snapshots/Rendering/HtmlTests/RendersTable.verified.txt\n" +
			"+++ .artifacts/.snapshots/received/Acme.Tests/Rendering/HtmlTests/RendersTable.received.txt\n" +
			"@@ line 1 @@\n a\n-b\n+B\n c\n");
	}

	[Test]
	public void DiffOfNewSnapshotIsAllAdditions()
	{
		Cli().Diff(New).Should().Be(ExitCode.Ok);
		_out.ToString().Should().Contain("(new snapshot, no verified file)").And.Contain("+new {x}");
	}

	[Test]
	public void DiffTakesOneSnapshot()
	{
		Cli().Diff("HtmlTests/*").Should().Be(ExitCode.Usage);
		_err.ToString().Should().Contain("matches 2 snapshots");
	}

	[Test]
	public void AcceptWritesVerifiedNormalisedAndDeletesReceived()
	{
		Cli().Accept([New], all: false).Should().Be(ExitCode.Ok);
		_fs.File.ReadAllText(Verified("Rendering", "HtmlTests", "RendersList.a1b2c3d4.verified.txt")).Should().Be("new {x}\n");
		_fs.File.Exists(Received("Rendering", "HtmlTests", "RendersList.a1b2c3d4.received.txt")).Should().BeFalse();
		_fs.Directory.EnumerateFiles(Verified(), "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
	}

	[Test]
	public void AcceptOverwritesChangedVerified()
	{
		Cli().Accept(["HtmlTests/RendersTable"], all: false).Should().Be(ExitCode.Ok);
		_fs.File.ReadAllText(Verified("Rendering", "HtmlTests", "RendersTable.verified.txt")).Should().Be("a\nB\nc\n");
	}

	[Test]
	public void AcceptAll()
	{
		Cli().Accept([], all: true).Should().Be(ExitCode.Ok);
		_fs.File.ReadAllText(P("src", "Lib", ".snapshots", "X", "Y.verified.txt")).Should().Be("obj\n");
		_out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);
		Cli().Status(json: false).Should().Be(ExitCode.Ok);
	}

	[Test]
	public void FileKeyedSnapshotsAreFoundAndAcceptedBySuffix()
	{
		// Snapshot.MatchFile keys by a source file's path, so there is no class or method segment.
		_fs.AddFile(Received("journeys", "checkout.received.journey"), new("## get url\n"));
		_fs.AddFile(Received("landing.received.journey"), new("## get title\n"));

		Cli().Status(json: true).Should().Be(ExitCode.Pending);
		using (var document = JsonDocument.Parse(_out.ToString()))
		{
			document.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString())
				.Should().Contain(["Acme.Tests/journeys/checkout", "Acme.Tests/landing"]);
		}

		Cli().Accept(["checkout", "Acme.Tests/landing"], all: false).Should().Be(ExitCode.Ok);
		_fs.File.ReadAllText(Verified("journeys", "checkout.verified.journey")).Should().Be("## get url\n");
		_fs.File.ReadAllText(Verified("landing.verified.journey")).Should().Be("## get title\n");
	}

	[Test]
	public void RejectDeletesReceivedAndKeepsVerified()
	{
		Cli().Reject([Changed], all: false).Should().Be(ExitCode.Ok);
		_fs.File.Exists(Received("Rendering", "HtmlTests", "RendersTable.received.txt")).Should().BeFalse();
		_fs.File.ReadAllText(Verified("Rendering", "HtmlTests", "RendersTable.verified.txt")).Should().Be("a\nb\nc\n");
	}

	[Test]
	[Arguments("RendersTable", new[] { Changed })]
	[Arguments("Acme.Tests/Rendering/HtmlTests/RendersTable", new[] { Changed })]
	[Arguments("HtmlTests/*", new[] { New, Changed })]
	[Arguments("**/Render*", new[] { New, Changed })]
	[Arguments("Acme.Tests/**", new[] { New, Changed })]
	[Arguments("Render?Table", new[] { Changed })]
	[Arguments("Y", new[] { Obj })]
	public void SelectsByKeySuffixOrGlob(string pattern, string[] expected)
	{
		var cli = Cli();
		cli.Accept([pattern], all: false).Should().Be(ExitCode.Ok);
		_out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split("  ")[1]).Should().BeEquivalentTo(expected);
	}

	[Test]
	[Arguments("Tables")]
	[Arguments("HtmlTests")]
	[Arguments("ble")]
	public void UnknownKeyIsAUsageError(string pattern)
	{
		Cli().Accept([pattern], all: false).Should().Be(ExitCode.Usage);
		_err.ToString().Should().Contain($"No pending snapshot matches '{pattern}'");
	}

	[Test]
	public void AmbiguousSuffixIsAUsageError()
	{
		_fs.AddFile(P(".artifacts", ".snapshots", "received", "Acme.Tests", "Other", "HtmlTests", "RendersTable.received.txt"), new("x"));
		Cli().Accept(["HtmlTests/RendersTable"], all: false).Should().Be(ExitCode.Usage);
		_err.ToString().Should().Contain("is ambiguous");
		_fs.File.ReadAllText(Verified("Rendering", "HtmlTests", "RendersTable.verified.txt")).Should().Be("a\nb\nc\n");
	}

	[Test]
	public void KeysAndAllTogetherIsAUsageError() => Cli().Accept([Changed], all: true).Should().Be(ExitCode.Usage);

	[Test]
	public void NeitherKeysNorAllIsAUsageError() => Cli().Reject([], all: false).Should().Be(ExitCode.Usage);

	[Test]
	public void MissingRootIsAUsageError() => Cli(P("missing")).Status(json: false).Should().Be(ExitCode.Usage);

	[Test]
	public void MarkerPointingOutsideTheRootIsSkipped()
	{
		_fs.File.WriteAllText(Received(ReceivedMarker.FileName), "project=Acme.Tests\nverified=../../../../../elsewhere/.snapshots\n");
		Cli().Accept([], all: true).Should().Be(ExitCode.Ok);
		_err.ToString().Should().Contain("is outside");
		_fs.Directory.Exists(_fs.Path.Combine(_fs.Path.GetPathRoot(Root)!, "elsewhere")).Should().BeFalse();
		_out.ToString().Should().Contain(Obj).And.NotContain(Changed);
	}

	private void Manifest(params string[] files) =>
		_fs.File.WriteAllText(Received(ReceivedMarker.FileName.Replace("project", "manifest", StringComparison.Ordinal)),
			"# verified files touched by the test run that started 2026-11-03T15:00:00Z\n" + string.Concat(files.Select(f => f + "\n")));

	[Test]
	public void OrphansAreVerifiedFilesTheLastRunDidNotTouch()
	{
		_fs.AddFile(Verified("Old", "Gone.verified.txt"), new("x"));
		Manifest("Rendering/HtmlTests/RendersTable.verified.txt");
		Cli().Orphans(json: false, delete: false).Should().Be(ExitCode.Pending);
		_out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Should().MatchSnapshot();
		_err.ToString().Should().Contain("No run manifest for Lib");
		_fs.File.Exists(Verified("Old", "Gone.verified.txt")).Should().BeTrue();
	}

	[Test]
	public void OrphansJsonAndDelete()
	{
		_fs.AddFile(Verified("Old", "Gone.verified.txt"), new("x"));
		Manifest("Rendering/HtmlTests/RendersTable.verified.txt");
		Cli().Orphans(json: true, delete: false).Should().Be(ExitCode.Pending);
		_out.ToString().Should().Contain("\"verified\": \"tests/Acme.Tests/.snapshots/Old/Gone.verified.txt\"");

		Cli().Orphans(json: false, delete: true).Should().Be(ExitCode.Ok);
		_fs.File.Exists(Verified("Old", "Gone.verified.txt")).Should().BeFalse();
		_fs.File.Exists(Verified("Rendering", "HtmlTests", "RendersTable.verified.txt")).Should().BeTrue();
	}

	[Test]
	public void NoOrphansWhenEverythingWasTouched()
	{
		Manifest("Rendering/HtmlTests/RendersTable.verified.txt");
		Cli().Orphans(json: false, delete: false).Should().Be(ExitCode.Ok);
		_out.ToString().Should().Contain("No orphaned snapshots.");
	}

	[Test]
	public void ReviewAcceptsRejectsAndSkips()
	{
		var prompt = new ScriptedPrompt('x', 'a', 's', 'r');
		Cli().Review(prompt, color: false).Should().Be(ExitCode.Pending);
		_fs.File.ReadAllText(Verified("Rendering", "HtmlTests", "RendersList.a1b2c3d4.verified.txt")).Should().Be("new {x}\n");
		_fs.File.Exists(Received("Rendering", "HtmlTests", "RendersTable.received.txt")).Should().BeTrue("it was skipped");
		_fs.File.Exists(P("src", "Lib", "obj", "snapshots", "received", "X", "Y.received.txt")).Should().BeFalse("it was rejected");
		prompt.Asked.Should().Be(4, "the unknown answer is asked again");
		_out.ToString().Should().Contain("1 accepted, 1 rejected, 1 still pending.").And.NotContain("\u001b");
	}

	[Test]
	public void ReviewStopsOnQuitOrEndOfInput()
	{
		var quit = new ScriptedPrompt('q', 'a', 'a');
		Cli().Review(quit, color: false).Should().Be(ExitCode.Pending);
		quit.Asked.Should().Be(1);
		_out.ToString().Should().Contain("0 accepted, 0 rejected, 3 still pending.");
		Cli().Review(new ScriptedPrompt(), color: false).Should().Be(ExitCode.Pending);
	}

	[Test]
	public void ReviewColoursOnlyWhenAsked()
	{
		Cli().Review(new ScriptedPrompt('s', 's', 's'), color: true).Should().Be(ExitCode.Pending);
		_out.ToString().Should().Contain("\u001b[31m-b\u001b[0m").And.Contain("\u001b[32m+B\u001b[0m");
	}

	[Test]
	public void ReviewWithNothingPending()
	{
		var empty = new MockFileSystem();
		empty.AddDirectory(Root);
		new SnapCli(empty, Root, _out, _err).Review(new ScriptedPrompt(), color: false).Should().Be(ExitCode.Ok);
	}

	private sealed class ScriptedPrompt(params char[] answers) : ISnapPrompt
	{
		public int Asked { get; private set; }

		public char? Ask(string question) => Asked < answers.Length ? answers[Asked++] : null;
	}

	// A PNG signature and IHDR length: CR, LF, SUB and NULs that text handling would rewrite.
	private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x0D, 0x0A];
	private static readonly byte[] OldPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0];

	private void AddBinary(bool withVerified = true)
	{
		if (withVerified)
			_fs.AddFile(Verified("Rendering", "ChartTests", "Draws.verified.png"), new(OldPng));
		_fs.AddFile(Received("Rendering", "ChartTests", "Draws.received.png"), new(Png));
		_fs.AddFile(Received("Rendering", "ChartTests", "Draws.received-diff.png"), new(Png));
	}

	// Drawing: a PNG snapshot's picture in `snap review`, where the terminal can show one.

	private static readonly byte[] DiffPng = [.. Png, 1];
	private static readonly byte[] ZoomPng = [.. Png, 2];
	private static readonly byte[] Blob = [1, 0, 2, 0, 3];

	private List<(byte[] Png, bool Zoom)> ReviewDrawing(bool drawn = true, char[]? answers = null)
	{
		var calls = new List<(byte[] Png, bool Zoom)>();
		Cli().Review(new ScriptedPrompt(answers ?? ['s', 's', 's', 's']), color: true, (png, zoom) =>
		{
			calls.Add((png, zoom));
			return drawn;
		});
		return calls;
	}

	[Test]
	public void ReviewDrawsTheDiffImageOfAChangedPicture()
	{
		AddBinary();
		_fs.AddFile(Received("Rendering", "ChartTests", "Draws.received-diff.png"), new(DiffPng));

		var calls = ReviewDrawing();

		var call = calls.Should().ContainSingle().Subject;
		call.Png.Should().Equal(DiffPng);
		call.Zoom.Should().BeFalse();
	}

	[Test]
	public void ReviewDrawsTheZoomRatherThanTheDiffWhenThereIsOne()
	{
		AddBinary();
		_fs.AddFile(Received("Rendering", "ChartTests", "Draws.received-diff.png"), new(DiffPng));
		_fs.AddFile(Received("Rendering", "ChartTests", "Draws.received-zoom.png"), new(ZoomPng));

		var calls = ReviewDrawing();

		var call = calls.Should().ContainSingle().Subject;
		call.Png.Should().Equal(ZoomPng);
		call.Zoom.Should().BeTrue();
	}

	[Test]
	public void ReviewDrawsTheReceivedPictureOfANewSnapshotAndOfOneWithNoDiff()
	{
		AddBinary(withVerified: false);
		_fs.File.Delete(Received("Rendering", "ChartTests", "Draws.received-diff.png"));

		var calls = ReviewDrawing();

		var call = calls.Should().ContainSingle().Subject;
		call.Png.Should().Equal(Png);
		call.Zoom.Should().BeFalse();
	}

	[Test]
	public void ReviewDrawsNothingForBinaryThatIsNotAPngOrForText()
	{
		_fs.AddFile(Received("Rendering", "ChartTests", "Blob.received.bin"), new(Blob));

		ReviewDrawing().Should().BeEmpty("the pending text and object snapshots and a blob that is not a PNG are not pictures");
	}

	[Test]
	public void ReviewWithoutAHookOrWhenDrawingFailsStillShowsThePathsAndAsksTheSame()
	{
		AddBinary();

		Cli().Review(new ScriptedPrompt('s', 's', 's', 's'), color: true, draw: null).Should().Be(ExitCode.Pending);
		var failed = ReviewDrawing(drawn: false);

		failed.Should().ContainSingle();
		_out.ToString().Should().Contain("Binary snapshot: no line diff, open the files to compare.").And.Contain("Draws.received-diff.png");
	}

	[Test]
	public void AcceptCopiesBinarySnapshotsByteForByteAndRemovesAttachments()
	{
		AddBinary();
		Cli().Accept(["ChartTests/Draws"], all: false).Should().Be(ExitCode.Ok);
		_fs.File.ReadAllBytes(Verified("Rendering", "ChartTests", "Draws.verified.png")).Should().Equal(Png);
		_fs.File.Exists(Received("Rendering", "ChartTests", "Draws.received.png")).Should().BeFalse();
		_fs.File.Exists(Received("Rendering", "ChartTests", "Draws.received-diff.png")).Should().BeFalse();
	}

	[Test]
	public void RejectRemovesAttachments()
	{
		AddBinary();
		Cli().Reject(["ChartTests/Draws"], all: false).Should().Be(ExitCode.Ok);
		_fs.File.ReadAllBytes(Verified("Rendering", "ChartTests", "Draws.verified.png")).Should().Equal(OldPng);
		_fs.File.Exists(Received("Rendering", "ChartTests", "Draws.received-diff.png")).Should().BeFalse();
	}

	[Test]
	public void AttachmentsAreNotPending()
	{
		AddBinary();
		Cli().Status(json: true).Should().Be(ExitCode.Pending);
		using var document = JsonDocument.Parse(_out.ToString());
		document.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString())
			.Should().Equal("Acme.Tests/Rendering/ChartTests/Draws", New, Changed, Obj);
	}

	[Test]
	public void DiffOfABinarySnapshotNamesSizesAndAttachments()
	{
		AddBinary();
		Cli().Diff("ChartTests/Draws").Should().Be(ExitCode.Ok);
		_out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Should().Be(
			"--- tests/Acme.Tests/.snapshots/Rendering/ChartTests/Draws.verified.png  (11 bytes)\n" +
			"+++ .artifacts/.snapshots/received/Acme.Tests/Rendering/ChartTests/Draws.received.png  (14 bytes)\n" +
			"Binary snapshot: no line diff, open the files to compare.\n" +
			"    .artifacts/.snapshots/received/Acme.Tests/Rendering/ChartTests/Draws.received-diff.png\n");
	}

	[Test]
	public void DiffOfANewBinarySnapshot()
	{
		AddBinary(withVerified: false);
		Cli().Diff("ChartTests/Draws").Should().Be(ExitCode.Ok);
		_out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Should().StartWith(
			"--- (new snapshot, no verified file)\n+++ .artifacts/.snapshots/received/Acme.Tests/Rendering/ChartTests/Draws.received.png  (14 bytes)\n");
	}

	[Test]
	public void IoErrorsExitThree()
	{
		var locked = Received("Rendering", "HtmlTests", "RendersTable.received.txt");
		_fs.File.SetAttributes(Verified("Rendering", "HtmlTests", "RendersTable.verified.txt"), FileAttributes.ReadOnly);
		Cli().Accept([Changed], all: false).Should().Be(ExitCode.Io);
		_err.ToString().Should().StartWith("Error: ");
		_fs.File.Exists(locked).Should().BeTrue();
	}
}
