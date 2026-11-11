using AwesomeAssertions;

namespace Nullean.Snapshots.Images.Tests;

/// <summary>The blue boxes drawn around the regions of a diff image.</summary>
public class OutlineTests
{
	private static bool IsBlue(RgbaImage image, int x, int y)
	{
		var at = ((y * image.Width) + x) * 4;
		return image.Pixels[at] == 0 && image.Pixels[at + 1] == 102 && image.Pixels[at + 2] == 255 && image.Pixels[at + 3] == 255;
	}

	private static RgbaImage Diff(RgbaImage verified, RgbaImage actual, bool outline)
	{
		var comparison = new ImageSnapshotFormat(new ImageSnapshotOptions { OutlineRegions = outline }).Compare(Png.Encode(verified), Png.Encode(actual));
		comparison.Comparison.IsMatch.Should().BeFalse();
		return Png.Decode(comparison.Attachments.Single().Content.Span);
	}

	private static readonly RgbaImage White = TestImages.Solid(20, 10, 255, 255, 255);

	[Test]
	public void NothingIsDrawnByDefault()
	{
		var diff = Diff(White, White.With(5, 4, 0, 0, 0), outline: false);

		Enumerable.Range(0, 10).SelectMany(y => Enumerable.Range(0, 20).Select(x => (x, y))).Should().NotContain(p => IsBlue(diff, p.x, p.y));
	}

	[Test]
	public void ARegionIsBoxedThreePixelsOutsideWithATwoPixelLine()
	{
		var diff = Diff(White, White.With(5, 4, 0, 0, 0), outline: true);

		// The differing pixel is (5,4): the box runs x 2-8, y 1-7, two pixels in.
		foreach (var (x, y) in new[] { (2, 1), (8, 1), (2, 7), (8, 7), (5, 1), (5, 2), (5, 7), (5, 6), (2, 4), (3, 4), (8, 4), (7, 4) })
			IsBlue(diff, x, y).Should().BeTrue($"({x},{y}) is on the box");
		foreach (var (x, y) in new[] { (5, 3), (5, 5), (4, 4), (6, 4), (1, 4), (9, 4), (5, 0), (5, 8) })
			IsBlue(diff, x, y).Should().BeFalse($"({x},{y}) is inside or outside it");
		IsBlue(diff, 5, 4).Should().BeFalse("the difference itself stays red, not blue");
	}

	[Test]
	public void ABoxAtTheEdgeIsClampedToTheImage()
	{
		var diff = Diff(White, White.With(0, 0, 0, 0, 0).With(19, 9, 0, 0, 0), outline: true);

		IsBlue(diff, 0, 3).Should().BeTrue();
		IsBlue(diff, 3, 0).Should().BeTrue();
		IsBlue(diff, 19, 6).Should().BeTrue();
		IsBlue(diff, 16, 9).Should().BeTrue();
	}

	[Test]
	public void EveryRegionGetsItsOwnBox()
	{
		var image = TestImages.Solid(80, 20, 255, 255, 255);
		var diff = Diff(image, image.With(5, 10, 0, 0, 0).With(70, 10, 0, 0, 0), outline: true);

		IsBlue(diff, 2, 10).Should().BeTrue();
		IsBlue(diff, 67, 10).Should().BeTrue();
		IsBlue(diff, 40, 10).Should().BeFalse("nothing is between the two places");
	}

	[Test]
	public void OnlyTheFirstHundredRegionsAreBoxed()
	{
		var diff = TestImages.Solid(1000, 20, 255, 255, 255);
		var regions = Enumerable.Range(0, PixelComparer.MaxOutlines + 5).Select(i => new DiffRegion(5 + (i * 9), 10, 5 + (i * 9), 10, 1)).ToList();

		var outlined = PixelComparer.Outline(diff, regions);

		IsBlue(outlined, 5 + ((PixelComparer.MaxOutlines - 1) * 9) - 3, 10).Should().BeTrue("the last of the first hundred");
		IsBlue(outlined, 5 + (PixelComparer.MaxOutlines * 9) - 3, 10).Should().BeFalse("the hundred and first is not drawn");
	}

	[Test]
	public void DrawingNeverChangesTheMatchOrTheRegionsReported()
	{
		var actual = White.With(5, 4, 0, 0, 0);

		var plain = new ImageSnapshotFormat().Compare(Png.Encode(White), Png.Encode(actual));
		var boxed = new ImageSnapshotFormat(new ImageSnapshotOptions { OutlineRegions = true }).Compare(Png.Encode(White), Png.Encode(actual));

		boxed.Comparison.Summary.Should().Be(plain.Comparison.Summary);
		boxed.Comparison.Differences.Should().BeEquivalentTo(plain.Comparison.Differences);
		new ImageSnapshotFormat(new ImageSnapshotOptions { OutlineRegions = true })
			.Compare(Png.Encode(White), Png.Encode(White))
			.Comparison
			.IsMatch
			.Should()
			.BeTrue();
	}
}
