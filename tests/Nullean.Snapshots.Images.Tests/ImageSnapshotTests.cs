using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Images.Tests;

public class ImageSnapshotTests
{
	private const string Key = "Rendering/HtmlTests/RendersTable";

	private static readonly RgbaImage White = TestImages.Solid(40, 30, 255, 255, 255);

	private static RgbaImage Changed()
	{
		var image = White;
		for (var x = 10; x < 14; x++)
			image = image.With(x, 5, 255, 0, 0);
		return image.With(35, 25, 0, 0, 0);
	}

	private static SnapshotResult Match(TestProject p, byte[] actual, ImageSnapshotOptions? options = null) =>
		p.Engine.Match(actual, new ImageSnapshotFormat(options), p.Site(), null, null);

	private static void Verified(TestProject p, byte[] png) => p.Fs.AddFile(p.VerifiedFile(Key, "png"), new MockFileData(png));

	private static string DiffFile(TestProject p) =>
		SnapshotPath.File(p.Fs, p.Project.ReceivedDirectory, Key, SnapshotPath.AttachmentInfix, ImageSnapshotFormat.DiffSuffix);

	[Test]
	public void TheSamePixelsMatchWhateverTheEncoder()
	{
		var p = new TestProject();
		Verified(p, Png.Encode(White));
		var other = TestImages.Encode(White, [1, 3, 4], textChunk: true, idatSplit: 3);
		other.Should().NotEqual(Png.Encode(White));

		Match(p, other).Passed.Should().BeTrue();
		p.Fs.File.ReadAllBytes(p.VerifiedFile(Key, "png")).Should().Equal(Png.Encode(White));
	}

	[Test]
	public void AMismatchWritesTheReceivedBytesAndADiffImage()
	{
		var p = new TestProject();
		Verified(p, Png.Encode(White));
		var actual = TestImages.Encode(Changed(), [4], textChunk: true);

		var result = Match(p, actual);

		result.Passed.Should().BeFalse();
		p.Fs.File.ReadAllBytes(p.ReceivedFile(Key, "png")).Should().Equal(actual);
		var diff = Png.Decode(p.Fs.File.ReadAllBytes(DiffFile(p)));
		diff.Pixels.AsSpan(((5 * 40) + 10) * 4, 4).ToArray().Should().Equal(255, 0, 0, 255);
	}

	[Test]
	public void FailureMessage()
	{
		var p = new TestProject();
		Verified(p, Png.Encode(White));
		Match(p, Png.Encode(Changed())).FailureMessage.Should().MatchSnapshot();
	}

	[Test]
	public void SizeMismatchMessage()
	{
		var p = new TestProject();
		Verified(p, Png.Encode(White));
		var result = Match(p, Png.Encode(TestImages.Solid(40, 31, 255, 255, 255)));
		result.FailureMessage.Should().MatchSnapshot();
		p.Fs.File.Exists(DiffFile(p)).Should().BeFalse();
	}

	[Test]
	public void AnInvalidVerifiedFileIsADifference()
	{
		var p = new TestProject();
		Verified(p, "not a png"u8.ToArray());
		var result = Match(p, Png.Encode(White));
		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("1 difference(s):\nthe verified file is not a valid PNG: Not a PNG");
	}

	[Test]
	public void AnInvalidActualImageThrowsBeforeWriting()
	{
		var p = new TestProject();
		var act = () => Match(p, [1, 2, 3]);
		act.Should().Throw<FormatException>();
		p.Fs.File.Exists(p.ReceivedFile(Key, "png")).Should().BeFalse();
	}

	[Test]
	public void APassingImageRemovesAnOldDiff()
	{
		var p = new TestProject();
		Verified(p, Png.Encode(White));
		Match(p, Png.Encode(Changed()));
		p.Fs.File.Exists(DiffFile(p)).Should().BeTrue();

		Match(p, Png.Encode(White)).Passed.Should().BeTrue();
		p.Fs.File.Exists(DiffFile(p)).Should().BeFalse();
		p.Fs.File.Exists(p.ReceivedFile(Key, "png")).Should().BeFalse();
	}

	[Test]
	[Arguments(4, 0.0, false)]
	[Arguments(5, 0.0, true)]
	[Arguments(0, 5.0 / 1200, true)]
	[Arguments(0, 4.0 / 1200, false)]
	[Arguments(5, 4.0 / 1200, true)]
	public void TheLargerAllowanceApplies(int pixels, double ratio, bool passes)
	{
		var p = new TestProject();
		Verified(p, Png.Encode(White));
		var options = new ImageSnapshotOptions { MaxDifferentPixels = pixels, MaxDifferentRatio = ratio };
		Match(p, Png.Encode(Changed()), options).Passed.Should().Be(passes);
	}

	[Test]
	public void OptionsAreValidated()
	{
		new Action(() => _ = new ImageSnapshotFormat(new() { Threshold = 1.5 })).Should().Throw<ArgumentOutOfRangeException>();
		new Action(() => _ = new ImageSnapshotFormat(new() { Threshold = double.NaN })).Should().Throw<ArgumentOutOfRangeException>();
		new Action(() => _ = new ImageSnapshotFormat(new() { MaxDifferentPixels = -1 })).Should().Throw<ArgumentOutOfRangeException>();
		new Action(() => _ = new ImageSnapshotFormat(new() { MaxDifferentRatio = 2 })).Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void TheAssertionsUseTheEngine()
	{
		var p = new TestProject();
		Snapshot.EngineOverride.Value = p.Engine;
		try
		{
			p.Fs.AddFile(SnapshotPath.File(p.Fs, p.Project.SnapshotsDirectory, "Rendering/HtmlTests/Draws", SnapshotPath.VerifiedInfix, "png"),
				new MockFileData(Png.Encode(White)));
			var bytes = Png.Encode(White);
			var pass = () => bytes.Should().MatchImageSnapshot(callerFilePath: p.TestFile, callerMemberName: "Draws");
			pass.Should().NotThrow();

			using var stream = new MemoryStream(Png.Encode(Changed()));
			var fail = () => stream.Should().MatchImageSnapshot(name: "stream", callerFilePath: p.TestFile, callerMemberName: "Draws");
			fail.Should().Throw<Exception>().WithMessage("Image snapshot is new*Draws.stream.verified.png*");
		}
		finally
		{
			Snapshot.EngineOverride.Value = null;
		}
	}
}
