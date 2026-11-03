using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;

namespace Nullean.Snapshots.Tests;

public class BinarySnapshotTests
{
	private const string Key = "Rendering/HtmlTests/RendersTable";

	// Bytes with CR, LF, a BOM and NULs: anything a text path would rewrite.
	private static readonly byte[] Bytes = [0xEF, 0xBB, 0xBF, 0x00, (byte)'\r', (byte)'\n', 0xFF, (byte)'\r'];

	[Test]
	public void NewSnapshotWritesTheBytesUnchanged()
	{
		var p = new TestProject();
		var result = p.Engine.Match(Bytes, new ByteFormat(), p.Site(), null, null);

		result.Passed.Should().BeFalse();
		p.Fs.File.ReadAllBytes(p.ReceivedFile(Key, "bin")).Should().Equal(Bytes);
		result.FailureMessage.Should().StartWith("Bytes snapshot is new: no verified file at tests/Acme.Tests/.snapshots/Rendering/HtmlTests/RendersTable.verified.bin");
		result.FailureMessage.Should().NotContain("Diff:");
		p.Fs.File.Exists(Attachment(p)).Should().BeFalse();
	}

	[Test]
	public void MismatchWritesReceivedAndTheAttachmentAndNamesIt()
	{
		var p = new TestProject();
		AddVerified(p, [1, 2, 3]);

		var result = p.Engine.Match(Bytes, new ByteFormat(), p.Site(), null, null);

		result.Passed.Should().BeFalse();
		p.Fs.File.ReadAllBytes(p.ReceivedFile(Key, "bin")).Should().Equal(Bytes);
		p.Fs.File.ReadAllBytes(Attachment(p)).Should().Equal([9, 9]);
		p.Fs.File.ReadAllBytes(p.VerifiedFile(Key, "bin")).Should().Equal([1, 2, 3]);
		result.FailureMessage.Should().Contain(
			"Received: .artifacts/.snapshots/received/Acme.Tests/Rendering/HtmlTests/RendersTable.received.bin\n"
			+ "Diff:     .artifacts/.snapshots/received/Acme.Tests/Rendering/HtmlTests/RendersTable.received-diff.bin\n");
	}

	[Test]
	public void MatchDeletesStaleReceivedAndAttachmentsAndNeverRewritesVerified()
	{
		var p = new TestProject();
		AddVerified(p, Bytes);
		p.Fs.AddFile(p.ReceivedFile(Key, "bin"), new MockFileData([7]));
		p.Fs.AddFile(Attachment(p), new MockFileData([7]));
		var verifiedWritten = p.Fs.File.GetLastWriteTimeUtc(p.VerifiedFile(Key, "bin"));

		var result = p.Engine.Match(Bytes, new ByteFormat(), p.Site(), null, null);

		result.Passed.Should().BeTrue();
		p.Fs.File.Exists(p.ReceivedFile(Key, "bin")).Should().BeFalse();
		p.Fs.File.Exists(Attachment(p)).Should().BeFalse();
		p.Fs.File.GetLastWriteTimeUtc(p.VerifiedFile(Key, "bin")).Should().Be(verifiedWritten);
	}

	[Test]
	public void AcceptWritesTheBytesAsVerifiedAndCleansUp()
	{
		var p = new TestProject();
		p.Environment["SNAPSHOT_ACCEPT"] = "1";
		AddVerified(p, [1]);
		p.Fs.AddFile(Attachment(p), new MockFileData([7]));

		var result = p.Engine.Match(Bytes, new ByteFormat(), p.Site(), null, null);

		result.Passed.Should().BeTrue();
		p.Fs.File.ReadAllBytes(p.VerifiedFile(Key, "bin")).Should().Equal(Bytes);
		p.Fs.File.Exists(Attachment(p)).Should().BeFalse();
	}

	[Test]
	public void AcceptIsIgnoredInCi()
	{
		var p = new TestProject();
		p.Environment["SNAPSHOT_ACCEPT"] = "1";
		p.Environment["CI"] = "true";

		var result = p.Engine.Match(Bytes, new ByteFormat(), p.Site(), null, null);

		result.Passed.Should().BeFalse();
		p.Fs.File.Exists(p.VerifiedFile(Key, "bin")).Should().BeFalse();
	}

	[Test]
	public void ANewFailureRemovesAnAttachmentFromAnEarlierRun()
	{
		var p = new TestProject();
		p.Fs.AddFile(Attachment(p), new MockFileData([7]));
		p.Engine.Match(Bytes, new ByteFormat(), p.Site(), null, null);
		p.Fs.File.Exists(Attachment(p)).Should().BeFalse();
	}

	[Test]
	public void InvalidValueThrowsBeforeAnythingIsWritten()
	{
		var p = new TestProject();
		var act = () => p.Engine.Match(new byte[] { 0xBA, 0xD0 }, new ByteFormat(), p.Site(), null, null);
		act.Should().Throw<FormatException>();
		p.Fs.File.Exists(p.ReceivedFile(Key, "bin")).Should().BeFalse();
	}

	[Test]
	public void AnUndeclaredAttachmentIsAnError()
	{
		var p = new TestProject();
		AddVerified(p, [1]);
		var act = () => p.Engine.Match(Bytes, new ByteFormat(undeclared: true), p.Site(), null, null);
		act.Should().Throw<InvalidOperationException>().WithMessage("*'oops.bin'*AttachmentSuffixes*");
		p.Fs.File.Exists(p.ReceivedFile(Key, "bin")).Should().BeFalse();
	}

	[Test]
	public void BinaryAndTextSnapshotsShareKeyClaims()
	{
		var p = new TestProject();
		p.Engine.Match(Bytes, new ByteFormat(), p.Site(line: 10), null, null);
		var act = () => p.Match("x", line: 11);
		act.Should().Throw<InvalidOperationException>().WithMessage("*pass a name*");
	}

	[Test]
	public void AttachmentsAreNotSnapshotsOfTheirOwn()
	{
		SnapshotPath.IsAttachmentOf("X.received-diff.png", "X.received.png").Should().BeTrue();
		SnapshotPath.IsAttachmentOf("X.received.png", "X.received.png").Should().BeFalse();
		SnapshotPath.IsAttachmentOf("X.received-diff.png.ab12.tmp", "X.received.png").Should().BeFalse();
		SnapshotPath.IsAttachmentOf("XY.received-diff.png", "X.received.png").Should().BeFalse();
		// A snapshot named 'received-x' is its own snapshot, not X's attachment.
		SnapshotPath.IsAttachmentOf("X.received-x.received.png", "X.received.png").Should().BeFalse();
	}

	[Test]
	public void BinaryDetectionFollowsGit()
	{
		SnapshotPath.IsBinary([0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0]).Should().BeTrue();
		SnapshotPath.IsBinary("plain text\n"u8).Should().BeFalse();
		SnapshotPath.IsBinary([]).Should().BeFalse();
		var lateNul = new byte[9000];
		lateNul.AsSpan().Fill((byte)'a');
		lateNul[8500] = 0;
		SnapshotPath.IsBinary(lateNul).Should().BeFalse();
	}

	private static string Attachment(TestProject p) =>
		SnapshotPath.File(p.Fs, p.Project.ReceivedDirectory, Key, SnapshotPath.AttachmentInfix, "diff.bin");

	private static void AddVerified(TestProject p, byte[] content) => p.Fs.AddFile(p.VerifiedFile(Key, "bin"), new MockFileData(content));

	/// <summary>Compares bytes exactly and attaches <c>[9, 9]</c> on a mismatch.</summary>
	private sealed class ByteFormat(bool undeclared = false) : BinarySnapshotFormat
	{
		public override string Name => "Bytes";
		public override string Extension => "bin";
		public override IReadOnlyList<string> AttachmentSuffixes => ["diff.bin"];

		public override void Validate(ReadOnlySpan<byte> actual)
		{
			if (actual.StartsWith((ReadOnlySpan<byte>)[0xBA, 0xD0]))
				throw new FormatException("bad");
		}

		public override BinarySnapshotComparison Compare(ReadOnlySpan<byte> verified, ReadOnlySpan<byte> actual) =>
			verified.SequenceEqual(actual)
				? BinarySnapshotComparison.Match
				: new(new SnapshotComparison(false, [["bytes differ"]]), [new SnapshotAttachment("Diff", undeclared ? "oops.bin" : "diff.bin", new byte[] { 9, 9 })]);
	}
}
