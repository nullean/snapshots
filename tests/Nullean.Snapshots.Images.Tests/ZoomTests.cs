using AwesomeAssertions;

namespace Nullean.Snapshots.Images.Tests;

/// <summary>The zoomed view of a diff image: cropped to the changes, enlarged by a whole number.</summary>
public class ZoomTests
{
	private static readonly RgbaImage Page = TestImages.Solid(200, 100, 255, 255, 255);

	private static BinarySnapshotComparison Compare(RgbaImage verified, RgbaImage actual, bool zoom, bool outline = false) =>
		new ImageSnapshotFormat(new ImageSnapshotOptions { ZoomOnChanges = zoom, OutlineRegions = outline }).Compare(Png.Encode(verified), Png.Encode(actual));

	private static RgbaImage Attachment(BinarySnapshotComparison comparison, string suffix) =>
		Png.Decode(comparison.Attachments.Single(a => a.Suffix == suffix).Content.Span);

	[Test]
	public void NoZoomIsWrittenUnlessAsked()
	{
		var comparison = Compare(Page, Page.With(100, 50, 0, 0, 0), zoom: false);

		comparison.Attachments.Select(a => a.Suffix).Should().Equal(ImageSnapshotFormat.DiffSuffix);
		new ImageSnapshotFormat().AttachmentSuffixes.Should().Equal(ImageSnapshotFormat.DiffSuffix);
	}

	[Test]
	public void AZoomIsDeclaredAsAnAttachmentWhenAsked() =>
		new ImageSnapshotFormat(new ImageSnapshotOptions { ZoomOnChanges = true }).AttachmentSuffixes.Should().Equal(ImageSnapshotFormat.DiffSuffix, ImageSnapshotFormat.ZoomSuffix);

	[Test]
	public void TheZoomIsTheChangedPlacePaddedAndEnlargedByAWholeNumber()
	{
		var comparison = Compare(Page, Page.With(100, 50, 0, 0, 0), zoom: true);

		var diff = Attachment(comparison, ImageSnapshotFormat.DiffSuffix);
		var zoom = Attachment(comparison, ImageSnapshotFormat.ZoomSuffix);

		// The pixel is (100,50); 16 pixels of padding make a 33x33 crop from (84,34), enlarged 8 times (the most).
		(zoom.Width, zoom.Height).Should().Be((33 * PixelComparer.MaxZoomFactor, 33 * PixelComparer.MaxZoomFactor));
		for (var y = 0; y < zoom.Height; y += 5)
		{
			for (var x = 0; x < zoom.Width; x += 5)
			{
				var from = (((34 + (y / 8)) * diff.Width) + 84 + (x / 8)) * 4;
				var at = ((y * zoom.Width) + x) * 4;
				zoom.Pixels.AsSpan(at, 4).ToArray().Should().Equal(diff.Pixels.AsSpan(from, 4).ToArray(), $"zoom ({x},{y}) is diff ({84 + (x / 8)},{34 + (y / 8)})");
			}
		}
	}

	[Test]
	public void TheZoomKeepsTheBoxesOfTheDiff()
	{
		var zoom = Attachment(Compare(Page, Page.With(100, 50, 0, 0, 0), zoom: true, outline: true), ImageSnapshotFormat.ZoomSuffix);

		Enumerable.Range(0, zoom.PixelCount).Count(i => zoom.Pixels[i * 4] == 0 && zoom.Pixels[(i * 4) + 1] == 102 && zoom.Pixels[(i * 4) + 2] == 255).Should().BeGreaterThan(0);
	}

	[Test]
	public void NoZoomWhenTheChangesAlreadyCoverMostOfTheImage()
	{
		var small = TestImages.Solid(20, 10, 255, 255, 255);

		Compare(small, small.With(5, 4, 0, 0, 0), zoom: true).Attachments.Select(a => a.Suffix).Should().Equal(new[] { ImageSnapshotFormat.DiffSuffix }, "the padded crop is the whole image");
		var wide = TestImages.Solid(400, 100, 255, 255, 255);
		Compare(wide, wide.With(5, 5, 0, 0, 0).With(395, 95, 0, 0, 0), zoom: true).Attachments.Select(a => a.Suffix).Should()
			.Equal(new[] { ImageSnapshotFormat.DiffSuffix }, "two places in opposite corners make the crop the whole picture");

		// Area is what counts: two places far apart on one line are a thin strip, which is zoomed (the strip's whole width, 2 times).
		var strip = Attachment(Compare(wide, wide.With(5, 50, 0, 0, 0).With(395, 50, 0, 0, 0), zoom: true), ImageSnapshotFormat.ZoomSuffix);
		(strip.Width, strip.Height).Should().Be((800, 66));
	}

	[Test]
	public void ASmallChangeInABigImageIsEnlargedAndABigOneIsNot()
	{
		var big = TestImages.Solid(1000, 1000, 255, 255, 255);
		var region = (int Left, int Top, int Right, int Bottom) => new DiffRegion(Left, Top, Right, Bottom, 1);

		PixelComparer.Zoom(big, [region(500, 500, 500, 500)])!.Width.Should().Be(33 * PixelComparer.MaxZoomFactor);
		PixelComparer.Zoom(big, [region(200, 200, 800, 800)])!.Width.Should().Be(633, "a crop that is already wide is cropped, not enlarged");
		PixelComparer.Zoom(big, []).Should().BeNull();
	}

	[Test]
	public void AZoomNeverExceedsTheLargestSide()
	{
		var tall = TestImages.Solid(1000, 3000, 255, 255, 255);

		var zoom = PixelComparer.Zoom(tall, [new DiffRegion(500, 100, 519, 2900, 1)])!;

		// A 52 x 2833 crop enlarged even twice would be 5666 tall, past the cap, so it is not enlarged at all.
		(zoom.Width, zoom.Height).Should().Be((52, 2833));
		zoom.Height.Should().BeLessThanOrEqualTo(PixelComparer.MaxZoomSide);
	}
}
