using System.Runtime.InteropServices;

namespace Nullean.Snapshots.Images;

/// <summary>A rectangle of differing pixels, inclusive.</summary>
internal readonly record struct DiffRegion(int Left, int Top, int Right, int Bottom, int Pixels)
{
	public int Width => Right - Left + 1;
	public int Height => Bottom - Top + 1;
}

/// <summary>What a pixel comparison found.</summary>
/// <param name="DifferentPixels">Pixels that differ beyond the threshold and are not anti-aliasing.</param>
/// <param name="AntiAliasedPixels">Pixels that differ but were classed as anti-aliasing and left out.</param>
/// <param name="Regions">The differing pixels grouped into nearby rectangles, top to bottom, left to right.</param>
/// <param name="Diff">The diff image: the verified image faded, differences red, anti-aliasing yellow.</param>
internal sealed record PixelComparison(int DifferentPixels, int AntiAliasedPixels, IReadOnlyList<DiffRegion> Regions, RgbaImage? Diff);

/// <summary>
/// Compares two images of the same size the way people see them. A port of pixelmatch's method (ISC, Mapbox): each
/// pixel's difference is a weighted distance in YIQ, so it follows perceived brightness and colour rather than raw RGB
/// deltas, and a differing pixel is left out as anti-aliasing when its neighbourhood shows an edge that both images
/// draw. Floating point is <c>double</c> with a fixed order of operations, so the result is the same on every machine.
/// </summary>
internal static class PixelComparer
{
	/// <summary>The largest YIQ distance, between black and white: <c>35215</c>.</summary>
	private const double MaxYiqDelta = 35215;

	/// <summary>How much of the verified image shows through in the diff image.</summary>
	private const double FadeAlpha = 0.1;

	/// <summary>Regions group differences that lie within cells of this size of each other.</summary>
	private const int Cell = 8;

	/// <summary>How far outside a region its outline is drawn.</summary>
	internal const int OutlineMargin = 3;

	/// <summary>How thick an outline is.</summary>
	internal const int OutlineThickness = 2;

	/// <summary>The most regions <see cref="Outline"/> draws around.</summary>
	internal const int MaxOutlines = 100;

	/// <summary>The outline colour: blue, which neither the red of a difference nor the yellow of anti-aliasing is.</summary>
	internal static readonly (byte R, byte G, byte B) OutlineColour = (0, 102, 255);

	/// <summary>
	/// A copy of <paramref name="diff"/> with a rectangle around each of the first <see cref="MaxOutlines"/> of
	/// <paramref name="regions"/>, <see cref="OutlineMargin"/> pixels outside it, clamped to the image.
	/// </summary>
	public static RgbaImage Outline(RgbaImage diff, IReadOnlyList<DiffRegion> regions)
	{
		var pixels = (byte[])diff.Pixels.Clone();

		void Set(int x, int y)
		{
			var at = ((y * diff.Width) + x) * 4;
			pixels[at] = OutlineColour.R;
			pixels[at + 1] = OutlineColour.G;
			pixels[at + 2] = OutlineColour.B;
			pixels[at + 3] = 255;
		}

		foreach (var region in regions.Take(MaxOutlines))
		{
			int left = Math.Max(0, region.Left - OutlineMargin), top = Math.Max(0, region.Top - OutlineMargin);
			int right = Math.Min(diff.Width - 1, region.Right + OutlineMargin), bottom = Math.Min(diff.Height - 1, region.Bottom + OutlineMargin);
			for (var t = 0; t < OutlineThickness; t++)
			{
				int l = left + t, r = right - t, u = top + t, d = bottom - t;
				if (l > r || u > d)
					break;
				for (var x = l; x <= r; x++)
				{
					Set(x, u);
					Set(x, d);
				}

				for (var y = u; y <= d; y++)
				{
					Set(l, y);
					Set(r, y);
				}
			}
		}

		return diff with { Pixels = pixels };
	}

	/// <summary>How much of the image around the changed places a zoom keeps.</summary>
	internal const int ZoomPadding = 32;

	/// <summary>
	/// The diff cropped to all of <paramref name="regions"/> and <see cref="ZoomPadding"/> pixels around them, at its own size: not
	/// enlarged, so every pixel is a pixel of the screenshot. <see langword="null"/> when there is no region, or the crop covers more
	/// than half the image, where the diff image already shows it.
	/// </summary>
	public static RgbaImage? Zoom(RgbaImage diff, IReadOnlyList<DiffRegion> regions)
	{
		if (regions.Count == 0)
			return null;
		var left = Math.Max(0, regions.Min(r => r.Left) - ZoomPadding);
		var top = Math.Max(0, regions.Min(r => r.Top) - ZoomPadding);
		var right = Math.Min(diff.Width - 1, regions.Max(r => r.Right) + ZoomPadding);
		var bottom = Math.Min(diff.Height - 1, regions.Max(r => r.Bottom) + ZoomPadding);
		int width = right - left + 1, height = bottom - top + 1;
		if ((long)width * height * 2 > (long)diff.Width * diff.Height)
			return null;
		var pixels = new byte[width * height * 4];
		for (var y = 0; y < height; y++)
			Buffer.BlockCopy(diff.Pixels, (((top + y) * diff.Width) + left) * 4, pixels, y * width * 4, width * 4);
		return new RgbaImage(width, height, pixels);
	}

	public static PixelComparison Compare(RgbaImage verified, RgbaImage actual, double threshold, bool includeAntiAliasing, bool drawDiff)
	{
		if (verified.Width != actual.Width || verified.Height != actual.Height)
			throw new ArgumentException("Images must be the same size.", nameof(actual));

		// Equal bytes are equal pixels: a vectorised compare settles the common case.
		if (verified.Pixels.AsSpan().SequenceEqual(actual.Pixels))
			return new PixelComparison(0, 0, [], null);

		int width = verified.Width, height = verified.Height;
		var a32 = MemoryMarshal.Cast<byte, uint>(verified.Pixels.AsSpan());
		var b32 = MemoryMarshal.Cast<byte, uint>(actual.Pixels.AsSpan());
		var maxDelta = MaxYiqDelta * threshold * threshold;
		var output = drawDiff ? new byte[verified.Pixels.Length] : null;
		var cellsWide = (width + Cell - 1) / Cell;
		var cells = new CellBox[cellsWide * ((height + Cell - 1) / Cell)];
		int different = 0, antiAliased = 0;

		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var i = (y * width) + x;
				var delta = a32[i] == b32[i] ? 0 : ColorDelta(verified.Pixels, actual.Pixels, i, i, yOnly: false);
				if (delta > maxDelta)
				{
					if (
						!includeAntiAliasing &&
						(AntiAliased(verified.Pixels, x, y, width, height, a32, b32) || AntiAliased(actual.Pixels, x, y, width, height, b32, a32))
					)
					{
						antiAliased++;
						if (output is not null)
							Draw(output, i, 255, 255, 0);
					}
					else
					{
						different++;
						cells[(y / Cell * cellsWide) + (x / Cell)].Add(x, y);
						if (output is not null)
							Draw(output, i, 255, 0, 0);
					}
				}
				else if (output is not null)
				{
					var p = verified.Pixels.AsSpan(i * 4, 4);
					var gray = 255 + (((p[0] * 0.29889531) + (p[1] * 0.58662247) + (p[2] * 0.11448223) - 255) * FadeAlpha * p[3] / 255);
					var v = (byte)Math.Clamp(Math.Round(gray), 0, 255);
					Draw(output, i, v, v, v);
				}
			}
		}

		var regions = Regions(cells, cellsWide);
		return new PixelComparison(different, antiAliased, regions, output is null ? null : new RgbaImage(width, height, output));
	}

	private static void Draw(byte[] output, int i, byte r, byte g, byte b)
	{
		output[i * 4] = r;
		output[(i * 4) + 1] = g;
		output[(i * 4) + 2] = b;
		output[(i * 4) + 3] = 255;
	}

	/// <summary>
	/// The squared YIQ distance between pixel <paramref name="k"/> of one image and pixel <paramref name="m"/> of the
	/// other; with <paramref name="yOnly"/>, the signed brightness difference alone (positive when the first is
	/// brighter).
	/// Translucent pixels are blended over a background that varies by position, so a difference in a transparent area
	/// still shows whatever colour it is drawn on.
	/// </summary>
	private static double ColorDelta(byte[] img1, byte[] img2, int k, int m, bool yOnly)
	{
		int r1 = img1[k * 4], g1 = img1[(k * 4) + 1], b1 = img1[(k * 4) + 2], a1 = img1[(k * 4) + 3];
		int r2 = img2[m * 4], g2 = img2[(m * 4) + 1], b2 = img2[(m * 4) + 2], a2 = img2[(m * 4) + 3];
		double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
		var da = a1 - a2;
		if (dr == 0 && dg == 0 && db == 0 && da == 0)
			return 0;

		if (a1 < 255 || a2 < 255)
		{
			var rb = 48 + (159 * (k % 2));
			var gb = 48 + (159 * ((int)(k / 1.618033988749895) % 2));
			var bb = 48 + (159 * ((int)(k / 2.618033988749895) % 2));
			dr = ((r1 * a1) - (r2 * a2) - (rb * da)) / 255.0;
			dg = ((g1 * a1) - (g2 * a2) - (gb * da)) / 255.0;
			db = ((b1 * a1) - (b2 * a2) - (bb * da)) / 255.0;
		}

		var y = (dr * 0.29889531) + (dg * 0.58662247) + (db * 0.11448223);
		if (yOnly)
			return y;
		var i = (dr * 0.59597799) - (dg * 0.27417610) - (db * 0.32180189);
		var q = (dr * 0.21147017) - (dg * 0.52261711) + (db * 0.31114694);
		return (0.5053 * y * y) + (0.299 * i * i) + (0.1957 * q * q);
	}

	/// <summary>
	/// True when pixel (<paramref name="x1"/>, <paramref name="y1"/>) of <paramref name="img"/> looks like
	/// anti-aliasing: at most two neighbours share its colour, it sits between a darker and a brighter neighbour, and
	/// the darkest or the brightest of those lies in a flat area in both images.
	/// </summary>
	private static bool AntiAliased(byte[] img, int x1, int y1, int width, int height, ReadOnlySpan<uint> a32, ReadOnlySpan<uint> b32)
	{
		int x0 = Math.Max(x1 - 1, 0), y0 = Math.Max(y1 - 1, 0);
		int x2 = Math.Min(x1 + 1, width - 1), y2 = Math.Min(y1 + 1, height - 1);
		var pos = (y1 * width) + x1;
		var zeroes = x1 == x0 || x1 == x2 || y1 == y0 || y1 == y2 ? 1 : 0;
		double min = 0, max = 0;
		int minX = 0, minY = 0, maxX = 0, maxY = 0;

		for (var x = x0; x <= x2; x++)
		{
			for (var y = y0; y <= y2; y++)
			{
				if (x == x1 && y == y1)
					continue;
				var delta = ColorDelta(img, img, pos, (y * width) + x, yOnly: true);
				if (delta == 0)
				{
					zeroes++;
					if (zeroes > 2)
						return false;
				}
				else if (delta < min)
				{
					min = delta;
					minX = x;
					minY = y;
				}
				else if (delta > max)
				{
					max = delta;
					maxX = x;
					maxY = y;
				}
			}
		}

		if (min == 0 || max == 0)
			return false;

		return (HasManySiblings(a32, minX, minY, width, height) && HasManySiblings(b32, minX, minY, width, height)) ||
			(HasManySiblings(a32, maxX, maxY, width, height) && HasManySiblings(b32, maxX, maxY, width, height));
	}

	/// <summary>True when at least three neighbours of a pixel have exactly its colour: it lies in a flat area.</summary>
	private static bool HasManySiblings(ReadOnlySpan<uint> img, int x1, int y1, int width, int height)
	{
		int x0 = Math.Max(x1 - 1, 0), y0 = Math.Max(y1 - 1, 0);
		int x2 = Math.Min(x1 + 1, width - 1), y2 = Math.Min(y1 + 1, height - 1);
		var value = img[(y1 * width) + x1];
		var zeroes = x1 == x0 || x1 == x2 || y1 == y0 || y1 == y2 ? 1 : 0;
		for (var x = x0; x <= x2; x++)
		{
			for (var y = y0; y <= y2; y++)
			{
				if (x == x1 && y == y1)
					continue;
				if (img[(y * width) + x] == value)
					zeroes++;
				if (zeroes > 2)
					return true;
			}
		}

		return false;
	}

	/// <summary>The exact bounds and count of the differing pixels in one cell.</summary>
	private struct CellBox
	{
		public int Left, Top, Right, Bottom, Pixels;

		public void Add(int x, int y)
		{
			if (Pixels == 0)
			{
				Left = Right = x;
				Top = Bottom = y;
			}
			else
			{
				Left = Math.Min(Left, x);
				Right = Math.Max(Right, x);
				Top = Math.Min(Top, y);
				Bottom = Math.Max(Bottom, y);
			}

			Pixels++;
		}
	}

	/// <summary>
	/// Groups cells that hold differences and touch, diagonals included, so differences within about a cell of each
	/// other read as one region. Ordered by top, then left.
	/// </summary>
	private static List<DiffRegion> Regions(CellBox[] cells, int cellsWide)
	{
		var cellsHigh = cells.Length / cellsWide;
		var seen = new bool[cells.Length];
		var regions = new List<DiffRegion>();
		var stack = new Stack<int>();
		for (var start = 0; start < cells.Length; start++)
		{
			if (seen[start] || cells[start].Pixels == 0)
				continue;
			seen[start] = true;
			stack.Push(start);
			int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, pixels = 0;
			while (stack.Count > 0)
			{
				var c = stack.Pop();
				var box = cells[c];
				left = Math.Min(left, box.Left);
				top = Math.Min(top, box.Top);
				right = Math.Max(right, box.Right);
				bottom = Math.Max(bottom, box.Bottom);
				pixels += box.Pixels;
				int cx = c % cellsWide, cy = c / cellsWide;
				for (var ny = Math.Max(cy - 1, 0); ny <= Math.Min(cy + 1, cellsHigh - 1); ny++)
				{
					for (var nx = Math.Max(cx - 1, 0); nx <= Math.Min(cx + 1, cellsWide - 1); nx++)
					{
						var n = (ny * cellsWide) + nx;
						if (seen[n] || cells[n].Pixels == 0)
							continue;
						seen[n] = true;
						stack.Push(n);
					}
				}
			}

			regions.Add(new DiffRegion(left, top, right, bottom, pixels));
		}

		regions.Sort(static (a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));
		return regions;
	}
}
