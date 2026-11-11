using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Nullean.Snapshots.Images;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>Screenshots of one journey on two sites: which differ, and the diff image of each pair.</summary>
public class SiteImagesTests
{
	private static string Root => OperatingSystem.IsWindows() ? @"C:\received" : "/received";

	private static string Baseline => Path.Combine(Root, "shop.baseline.artifacts");

	private static string Target => Path.Combine(Root, "shop.target.artifacts");

	private static byte[] Solid(int width, int height, byte value, (int X, int Y)? dot = null)
	{
		var pixels = new byte[width * height * 4];
		for (var i = 0; i < pixels.Length; i += 4)
		{
			pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
			pixels[i + 3] = 255;
		}

		if (dot is var (x, y))
		{
			var at = ((y * width) + x) * 4;
			pixels[at] = 255;
			pixels[at + 1] = 0;
			pixels[at + 2] = 0;
		}

		return Png.Encode(new RgbaImage(width, height, pixels));
	}

	private static MockFileSystem Folders(params (string Side, string Name, byte[] Png)[] files)
	{
		var fs = new MockFileSystem();
		fs.AddDirectory(Baseline);
		fs.AddDirectory(Target);
		foreach (var (side, name, png) in files)
			fs.AddFile(Path.Combine(side == "baseline" ? Baseline : Target, name.Replace('/', Path.DirectorySeparatorChar)), new MockFileData(png));
		return fs;
	}

	[Test]
	public void IdenticalScreenshotsDifferInNothing()
	{
		var fs = Folders(("baseline", "nav.png", Solid(20, 10, 200)), ("target", "nav.png", Solid(20, 10, 200)));

		SiteImages.Compare(fs, Baseline, Target).Should().BeEmpty();
		fs.AllFiles.Should().NotContain(f => f.EndsWith(".diff.png", StringComparison.Ordinal), "no difference, no diff image");
	}

	[Test]
	public void ADifferingPixelIsADifferenceWithADiffImageNextToTheTargets()
	{
		var fs = Folders(("baseline", "nav.png", Solid(20, 10, 200)), ("target", "nav.png", Solid(20, 10, 200, (5, 4))));

		var difference = SiteImages.Compare(fs, Baseline, Target).Should().ContainSingle().Subject;

		difference.Name.Should().Be("nav.png");
		difference.Summary.Should().StartWith("1 of 200 pixels differ (0.5");
		difference.Regions.Should().ContainSingle().Which.Should().Be("x 5-5, y 4-4 (1x1): 1 pixel(s)");
		difference.DiffPath.Should().Be(Path.Combine(Target, "nav.diff.png"));
		var diff = Png.Decode(fs.File.ReadAllBytes(difference.DiffPath!));
		(diff.Width, diff.Height).Should().Be((20, 10));
	}

	private static bool HasBlue(byte[] png) =>
		Png.Decode(png) is var image && Enumerable.Range(0, image.PixelCount).Any(i => image.Pixels[i * 4] == 0 && image.Pixels[(i * 4) + 1] == 102 && image.Pixels[(i * 4) + 2] == 255);

	[Test]
	public void TheDiffImageBoxesEachPlaceUnlessOptionsSayOtherwise()
	{
		var fs = Folders(("baseline", "nav.png", Solid(20, 10, 200)), ("target", "nav.png", Solid(20, 10, 200, (5, 4))));

		var boxed = SiteImages.Compare(fs, Baseline, Target).Single();
		HasBlue(fs.File.ReadAllBytes(boxed.DiffPath!)).Should().BeTrue();

		var plain = SiteImages.Compare(fs, Baseline, Target, new ImageSnapshotOptions()).Single();
		HasBlue(fs.File.ReadAllBytes(plain.DiffPath!)).Should().BeFalse("explicit options are the caller's: no boxes unless they ask");
	}

	[Test]
	public void ABigPictureWithASmallChangeGetsAZoomNextToItsDiff()
	{
		var fs = Folders(("baseline", "nav.png", Solid(200, 100, 200)), ("target", "nav.png", Solid(200, 100, 200, (100, 50))));

		var difference = SiteImages.Compare(fs, Baseline, Target).Single();

		difference.ZoomPath.Should().Be(Path.Combine(Target, "nav.zoom.png"));
		var zoom = Png.Decode(fs.File.ReadAllBytes(difference.ZoomPath!));
		(zoom.Width, zoom.Height).Should().Be((65, 65), "a crop at its own size, not enlarged");
		SiteImages.Compare(fs, Baseline, Target).Select(d => d.Name).Should().Equal(new[] { "nav.png" }, "neither the diff nor the zoom of an earlier run is a screenshot");
	}

	[Test]
	public void ASmallPictureNeedsNoZoom()
	{
		var fs = Folders(("baseline", "nav.png", Solid(20, 10, 200)), ("target", "nav.png", Solid(20, 10, 200, (5, 4))));

		SiteImages.Compare(fs, Baseline, Target).Single().ZoomPath.Should().BeNull();
		fs.AllFiles.Should().NotContain(f => f.EndsWith(".zoom.png", StringComparison.Ordinal));
	}

	[Test]
	public void DifferentSizesAreADifferenceWithoutADiffImage()
	{
		var fs = Folders(("baseline", "nav.png", Solid(20, 10, 200)), ("target", "nav.png", Solid(20, 12, 200)));

		var difference = SiteImages.Compare(fs, Baseline, Target).Should().ContainSingle().Subject;

		difference.Summary.Should().Be("size: verified 20x10, actual 20x12");
		difference.DiffPath.Should().BeNull();
	}

	[Test]
	public void AScreenshotOnlyOneSideTookIsADifference()
	{
		var fs = Folders(("baseline", "only-baseline.png", Solid(4, 4, 1)), ("target", "only-target.png", Solid(4, 4, 1)), ("baseline", "both.png", Solid(4, 4, 1)), ("target", "both.png", Solid(4, 4, 1)));

		SiteImages.Compare(fs, Baseline, Target).Select(d => (d.Name, d.Summary)).Should().Equal(
			("only-baseline.png", "the target did not take it"),
			("only-target.png", "the baseline did not take it"));
	}

	[Test]
	public void ScreenshotsInFoldersArePairedByTheirPath()
	{
		var fs = Folders(("baseline", "a/nav.png", Solid(4, 4, 1)), ("target", "a/nav.png", Solid(4, 4, 1, (1, 1))), ("baseline", "b/nav.png", Solid(4, 4, 1)), ("target", "b/nav.png", Solid(4, 4, 1)));

		SiteImages.Compare(fs, Baseline, Target).Should().ContainSingle().Which.Name.Should().Be("a/nav.png");
	}

	[Test]
	public void ADiffImageFromAnEarlierRunIsNeverAScreenshot()
	{
		var fs = Folders(("baseline", "nav.png", Solid(4, 4, 1)), ("target", "nav.png", Solid(4, 4, 1, (1, 1))));
		_ = SiteImages.Compare(fs, Baseline, Target);

		SiteImages.Compare(fs, Baseline, Target).Select(d => d.Name).Should().Equal("nav.png");
	}

	[Test]
	public void ABrokenPngIsADifferenceNotACrash()
	{
		var fs = Folders(("baseline", "nav.png", Solid(4, 4, 1)), ("target", "nav.png", [1, 2, 3]));

		SiteImages.Compare(fs, Baseline, Target).Should().ContainSingle().Which.Summary.Should().StartWith("not a valid PNG");
	}

	[Test]
	public void AFolderThatDoesNotExistHasNothingToCompare()
	{
		var fs = new MockFileSystem();
		fs.AddDirectory(Baseline);

		SiteImages.Compare(fs, Baseline, Target).Should().BeEmpty();
	}
}
