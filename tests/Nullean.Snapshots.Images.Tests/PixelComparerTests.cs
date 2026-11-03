using AwesomeAssertions;
using CsCheck;

namespace Nullean.Snapshots.Images.Tests;

public class PixelComparerTests
{
	private static PixelComparison Compare(RgbaImage a, RgbaImage b, double threshold = 0.1, bool includeAntiAliasing = false) =>
		PixelComparer.Compare(a, b, threshold, includeAntiAliasing, drawDiff: true);

	[Test]
	public void EqualImagesMatchWithoutADiffImage()
	{
		var image = TestImages.Solid(8, 8, 10, 20, 30);
		var result = Compare(image, image with { Pixels = (byte[])image.Pixels.Clone() });
		result.DifferentPixels.Should().Be(0);
		result.Diff.Should().BeNull();
	}

	[Test]
	public void ASmallColourShiftIsBelowTheThreshold()
	{
		var a = TestImages.Solid(4, 4, 100, 100, 100);
		Compare(a, a.With(1, 1, 103, 103, 103)).DifferentPixels.Should().Be(0);
		Compare(a, a.With(1, 1, 130, 130, 130)).DifferentPixels.Should().Be(1);
		Compare(a, a.With(1, 1, 103, 103, 103), threshold: 0).DifferentPixels.Should().Be(1);
	}

	[Test]
	public void AChangedBlockIsOneRegionWithExactBounds()
	{
		var a = TestImages.Solid(40, 30, 255, 255, 255);
		var b = a.With(10, 5, 255, 0, 0).With(11, 5, 255, 0, 0).With(10, 6, 255, 0, 0).With(11, 6, 255, 0, 0);
		var result = Compare(a, b);
		result.DifferentPixels.Should().Be(4);
		result.Regions.Should().Equal(new DiffRegion(10, 5, 11, 6, 4));
	}

	[Test]
	public void DistantChangesAreSeparateRegionsInReadingOrder()
	{
		var a = TestImages.Solid(64, 64, 255, 255, 255);
		var b = a.With(60, 2, 0, 0, 0).With(3, 2, 0, 0, 0).With(30, 50, 0, 0, 0);
		Compare(a, b).Regions.Select(r => (r.Left, r.Top)).Should().Equal((3, 2), (60, 2), (30, 50));
	}

	[Test]
	public void NearbyChangesAcrossCellsAreOneRegion()
	{
		var a = TestImages.Solid(32, 32, 255, 255, 255);
		var b = a.With(6, 6, 0, 0, 0).With(9, 9, 0, 0, 0);
		Compare(a, b).Regions.Should().Equal(new DiffRegion(6, 6, 9, 9, 2));
	}

	[Test]
	public void AShiftedAntiAliasedEdgeIsLeftOut()
	{
		var (a, b) = Edge(128, 180);
		var result = Compare(a, b);
		result.DifferentPixels.Should().Be(0);
		result.AntiAliasedPixels.Should().Be(a.Height);
		result.Regions.Should().BeEmpty();

		Compare(a, b, includeAntiAliasing: true).DifferentPixels.Should().Be(a.Height);
	}

	[Test]
	public void AnEdgeMovingOnePixelReadsAsAntiAliasing()
	{
		// As in pixelmatch: grey still sits between black and white on flat areas in both images. This is the
		// leniency that lets glyph edges move between rasterisers.
		var (a, _) = Edge(128, 128);
		var b = Grow(a, 1);
		var result = Compare(a, b);
		result.DifferentPixels.Should().Be(0);
		result.AntiAliasedPixels.Should().BeGreaterThan(0);
	}

	[Test]
	public void AnEdgeMovingTwoPixelsIsADifference()
	{
		var (a, _) = Edge(128, 128);
		var result = Compare(a, Grow(a, 2));
		result.DifferentPixels.Should().Be(a.Height);
		result.Regions.Should().Equal(new DiffRegion(6, 0, 6, a.Height - 1, a.Height));
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public void AnEdgeCountsAsAntiAliasingOnlyBesideAFlatArea(bool swapped)
	{
		// One-pixel columns: green, black, grey (changed), white, blue. A neighbour of the grey pixel lies in a flat
		// area when at least three of its own neighbours share its colour; only the black pixels beside the extra
		// black pixel at (3, 3) have three, so only rows 3 to 5 read as anti-aliasing.
		// Swapped, black and white trade places, so the flat area is on the brighter side.
		byte near = swapped ? (byte)255 : (byte)0, far = swapped ? (byte)0 : (byte)255;
		RgbaImage Columns(byte grey)
		{
			var image = TestImages.Solid(9, 9, 0, 0, 255);
			for (var y = 0; y < 9; y++)
				image = image.With(3, y, 0, 255, 0).With(4, y, near, near, near).With(5, y, grey, grey, grey).With(6, y, far, far, far);
			return image.With(3, 3, near, near, near);
		}

		var result = Compare(Columns(128), Columns(180));
		result.AntiAliasedPixels.Should().Be(3);
		result.DifferentPixels.Should().Be(6);
	}

	[Test]
	public void APixelWithThreeEqualNeighboursIsNotAntiAliasing()
	{
		// A grey cluster on a black and white edge: (5, 4) has three grey neighbours in both images, so it is part of
		// a shape rather than the edge's anti-aliasing, and its change counts.
		RgbaImage Cluster(byte grey)
		{
			var image = TestImages.Solid(12, 9, 255, 255, 255);
			for (var y = 0; y < 9; y++)
			{
				for (var x = 0; x < 5; x++)
					image = image.With(x, y, 0, 0, 0);
			}

			return image.With(5, 3, grey, grey, grey).With(5, 4, grey, grey, grey).With(5, 5, grey, grey, grey).With(6, 4, grey, grey, grey);
		}

		var diff = Compare(Cluster(128), Cluster(180)).Diff!;
		Pixel(diff, 5, 4).Should().Equal(255, 0, 0, 255);
	}

	[Test]
	public void RegionsFollowDifferencesUpwardToo()
	{
		// Cells (0,0), (0,1), (1,2) and (2,1) touch in a chain that turns upward at the end.
		var a = TestImages.Solid(32, 32, 255, 255, 255);
		var b = a.With(4, 4, 0, 0, 0).With(4, 12, 0, 0, 0).With(12, 20, 0, 0, 0).With(20, 12, 0, 0, 0);
		Compare(a, b).Regions.Should().Equal(new DiffRegion(4, 4, 20, 20, 4));
	}

	[Test]
	public void TranslucentDifferencesShowOnAVaryingBackground()
	{
		// Pinned: translucent pixels are blended over a background that varies by position, so an opaque colour that
		// happens to equal the background in one place still shows in the others.
		var opaque = TestImages.Solid(16, 16, 48, 48, 48);
		var clear = TestImages.Solid(16, 16, 48, 48, 48, a: 0);
		Compare(opaque, clear, includeAntiAliasing: true).DifferentPixels.Should().Be(PinnedTranslucentCount);
	}

	private const int PinnedTranslucentCount = 255;

	[Test]
	public void FullyTransparentPixelsMatchWhateverTheirColour()
	{
		var a = TestImages.Solid(3, 3, 255, 0, 0, a: 0);
		var b = TestImages.Solid(3, 3, 0, 0, 255, a: 0);
		Compare(a, b).DifferentPixels.Should().Be(0);
	}

	[Test]
	public void TranslucencyChangesAreDifferences()
	{
		var a = TestImages.Solid(3, 3, 0, 0, 0, a: 255);
		Compare(a, a.With(1, 1, 0, 0, 0, a: 0)).DifferentPixels.Should().Be(1);
	}

	[Test]
	public void TheDiffImageMarksDifferencesRedAndAntiAliasingYellow()
	{
		var (a, b) = Edge(128, 180);
		b = b.With(10, 4, 255, 0, 0);
		var diff = Compare(a, b).Diff!;
		Pixel(diff, 10, 4).Should().Equal(255, 0, 0, 255);
		Pixel(diff, 5, 4).Should().Equal(255, 255, 0, 255);
		// Unchanged pixels are the verified image, faded: black becomes light grey.
		Pixel(diff, 0, 4).Should().Equal(230, 230, 230, 255);
	}

	[Test]
	public void ComparisonIsSymmetric() =>
		Gen.Select(TestImages.Image(16), Gen.Int[0, 3], Gen.Bool).Sample((a, seed, includeAa) =>
		{
			var b = Perturb(a, seed);
			var ab = PixelComparer.Compare(a, b, 0.1, includeAa, drawDiff: false);
			var ba = PixelComparer.Compare(b, a, 0.1, includeAa, drawDiff: false);
			return ab.DifferentPixels == ba.DifferentPixels && ab.AntiAliasedPixels == ba.AntiAliasedPixels;
		});

	[Test]
	public void AHigherThresholdNeverFindsMore() =>
		Gen.Select(TestImages.Image(16), Gen.Double[0, 1], Gen.Double[0, 1]).Sample((a, t1, t2) =>
		{
			var b = Perturb(a, 1);
			var (low, high) = t1 <= t2 ? (t1, t2) : (t2, t1);
			return PixelComparer.Compare(a, b, high, true, false).DifferentPixels <= PixelComparer.Compare(a, b, low, true, false).DifferentPixels;
		});

	[Test]
	public void RegionsCoverEveryDifference() =>
		Gen.Select(TestImages.Image(40), Gen.Int[0, 3]).Sample((a, seed) =>
		{
			var result = PixelComparer.Compare(a, Perturb(a, seed), 0.1, false, false);
			return result.Regions.Sum(r => r.Pixels) == result.DifferentPixels
				&& result.Regions.All(r => r.Left <= r.Right && r.Top <= r.Bottom && r.Right < a.Width && r.Bottom < a.Height);
		});

	/// <summary>A black block (columns 0–4), an anti-aliased grey column 5, white from column 6, in two greys.</summary>
	private static (RgbaImage A, RgbaImage B) Edge(byte greyA, byte greyB)
	{
		var a = TestImages.Solid(12, 8, 255, 255, 255);
		for (var y = 0; y < a.Height; y++)
		{
			for (var x = 0; x < 5; x++)
				a = a.With(x, y, 0, 0, 0);
		}

		var b = a;
		for (var y = 0; y < a.Height; y++)
		{
			a = a.With(5, y, greyA, greyA, greyA);
			b = b.With(5, y, greyB, greyB, greyB);
		}

		return (a, b);
	}

	/// <summary>Moves the black block's edge and its grey column right by <paramref name="columns"/>.</summary>
	private static RgbaImage Grow(RgbaImage a, int columns)
	{
		var b = a;
		for (var y = 0; y < a.Height; y++)
		{
			for (var x = 5; x < 5 + columns; x++)
				b = b.With(x, y, 0, 0, 0);
			b = b.With(5 + columns, y, 128, 128, 128);
		}

		return b;
	}

	private static RgbaImage Perturb(RgbaImage a, int seed)
	{
		var pixels = (byte[])a.Pixels.Clone();
		for (var i = seed; i < pixels.Length; i += 7 + seed)
			pixels[i] ^= (byte)(0x55 + seed);
		return a with { Pixels = pixels };
	}

	private static byte[] Pixel(RgbaImage image, int x, int y) => image.Pixels.AsSpan(((y * image.Width) + x) * 4, 4).ToArray();
}
