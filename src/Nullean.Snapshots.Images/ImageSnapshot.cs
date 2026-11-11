using System.Globalization;

namespace Nullean.Snapshots.Images;

/// <summary>
/// PNG snapshots compared by their pixels: decoded with the package's own codec, so metadata and encoder choices
/// never matter, and compared perceptually with anti-aliasing left out (see <see cref="ImageSnapshotOptions"/>).
/// </summary>
public sealed class ImageSnapshotFormat : BinarySnapshotFormat
{
	/// <summary>The suffix of the diff image written next to a received image.</summary>
	public const string DiffSuffix = "diff.png";

	/// <summary>The suffix of the zoomed diff written next to the diff image, when <see cref="ImageSnapshotOptions.ZoomOnChanges"/> asks for it.</summary>
	public const string ZoomSuffix = "zoom.png";

	private readonly ImageSnapshotOptions _options;

	/// <summary>A format that compares with <paramref name="options"/>, or the defaults.</summary>
	public ImageSnapshotFormat(ImageSnapshotOptions? options = null)
	{
		_options = options ?? ImageSnapshotOptions.Default;
		_options.Validate();
		AttachmentSuffixes = _options.ZoomOnChanges ? [DiffSuffix, ZoomSuffix] : [DiffSuffix];
	}

	/// <inheritdoc />
	public override string Name => "Image";

	/// <inheritdoc />
	public override string Extension => "png";

	/// <inheritdoc />
	public override IReadOnlyList<string> AttachmentSuffixes { get; }

	/// <summary>Throws a <see cref="FormatException"/> unless <paramref name="actual"/> is a PNG that decodes.</summary>
	public override void Validate(ReadOnlySpan<byte> actual) => Png.Decode(actual);

	/// <inheritdoc />
	public override BinarySnapshotComparison Compare(ReadOnlySpan<byte> verified, ReadOnlySpan<byte> actual)
	{
		RgbaImage expected;
		try
		{
			expected = Png.Decode(verified);
		}
		catch (FormatException e)
		{
			return Failed(null, ["the verified file is not a valid PNG: " + e.Message]);
		}

		var image = Png.Decode(actual);
		if (expected.Width != image.Width || expected.Height != image.Height)
			return Failed(null, [$"size: verified {Size(expected)}, actual {Size(image)}"]);

		var result = PixelComparer.Compare(expected, image, _options.Threshold, _options.IncludeAntiAliasing, drawDiff: true);
		var allowed = _options.Allowed(expected.PixelCount);
		if (result.DifferentPixels <= allowed)
			return BinarySnapshotComparison.Match;

		var summary =
			$"{N(result.DifferentPixels)} of {N(expected.PixelCount)} pixels differ ({Percent(result.DifferentPixels, expected.PixelCount)}), {N(allowed)} allowed";
		if (result.AntiAliasedPixels > 0)
			summary += $"; {N(result.AntiAliasedPixels)} anti-aliased pixel(s) left out";
		var differences = result
			.Regions
			.Select(r => (IReadOnlyList<string>)[$"x {r.Left}-{r.Right}, y {r.Top}-{r.Bottom} ({r.Width}x{r.Height}): {N(r.Pixels)} pixel(s)"])
			.ToList();
		var diff = _options.OutlineRegions ? PixelComparer.Outline(result.Diff!, result.Regions) : result.Diff!;
		var attachments = new List<SnapshotAttachment> { new("Diff", DiffSuffix, Png.Encode(diff)) };
		if (_options.ZoomOnChanges && PixelComparer.Zoom(diff, result.Regions) is { } zoom)
			attachments.Add(new SnapshotAttachment("Zoom", ZoomSuffix, Png.Encode(zoom)));
		return new BinarySnapshotComparison(new SnapshotComparison(false, differences, Summary: summary), attachments);
	}

	private static BinarySnapshotComparison Failed(string? summary, IReadOnlyList<string> difference) =>
		new(new SnapshotComparison(false, [difference], Summary: summary), []);

	private static string Size(RgbaImage image) => $"{image.Width}x{image.Height}";

	private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

	private static string Percent(long part, long whole)
	{
		var percent = 100.0 * part / whole;
		return percent is > 0 and < 0.01 ? "<0.01%" : percent.ToString("0.00", CultureInfo.InvariantCulture) + "%";
	}
}
