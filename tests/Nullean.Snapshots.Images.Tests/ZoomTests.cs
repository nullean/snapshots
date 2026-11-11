using AwesomeAssertions;

namespace Nullean.Snapshots.Images.Tests;

/// <summary>The zoomed view of a diff image: the diff cropped to the changes, pixel for pixel.</summary>
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
	public void TheZoomIsTheChangedPlaceAndItsSurroundingsAtTheirOwnSize()
	{
		var comparison = Compare(Page, Page.With(100, 50, 0, 0, 0), zoom: true);

		var diff = Attachment(comparison, ImageSnapshotFormat.DiffSuffix);
		var zoom = Attachment(comparison, ImageSnapshotFormat.ZoomSuffix);

		// The pixel is (100,50); 32 pixels of padding make a 65x65 crop from (68,18), not enlarged.
		(zoom.Width, zoom.Height).Should().Be((65, 65));
		for (var y = 0; y < zoom.Height; y++)
		{
			for (var x = 0; x < zoom.Width; x++)
			{
				var from = (((18 + y) * diff.Width) + 68 + x) * 4;
				var at = ((y * zoom.Width) + x) * 4;
				zoom.Pixels.AsSpan(at, 4).ToArray().Should().Equal(diff.Pixels.AsSpan(from, 4).ToArray(), $"zoom ({x},{y}) is diff ({68 + x},{18 + y})");
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

		// Area is what counts: two places far apart on one line are a thin strip, which is cropped (the strip's whole width).
		var tall = TestImages.Solid(1000, 400, 255, 255, 255);
		var strip = Attachment(Compare(tall, tall.With(5, 200, 0, 0, 0).With(995, 200, 0, 0, 0), zoom: true), ImageSnapshotFormat.ZoomSuffix);
		(strip.Width, strip.Height).Should().Be((1000, 65));
	}

	[Test]
	public void ACropIsClampedToTheImageAndNeverLargerThanIt()
	{
		var big = TestImages.Solid(1000, 1000, 255, 255, 255);

		var corner = PixelComparer.Zoom(big, [new DiffRegion(0, 0, 4, 4, 1)])!;
		var middle = PixelComparer.Zoom(big, [new DiffRegion(500, 500, 500, 500, 1)])!;
		var large = PixelComparer.Zoom(big, [new DiffRegion(200, 200, 800, 800, 1)])!;

		(corner.Width, corner.Height).Should().Be((37, 37), "clamped at the corner: 5 + 32");
		(middle.Width, middle.Height).Should().Be((65, 65));
		(large.Width, large.Height).Should().Be((665, 665), "a big change is cropped, never enlarged");
		PixelComparer.Zoom(big, []).Should().BeNull();
	}
}
