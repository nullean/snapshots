namespace Nullean.Snapshots.Images;

/// <summary>How closely an image must match its verified snapshot.</summary>
public sealed record ImageSnapshotOptions
{
	/// <summary>The defaults: threshold 0.1, anti-aliasing left out, no differing pixels allowed.</summary>
	public static ImageSnapshotOptions Default { get; } = new();

	/// <summary>
	/// How different two pixels may look before they count as different, from 0 (any change counts) to 1 (black and
	/// white are the same). Measured as a perceptual YIQ distance. The default, 0.1, ignores rounding and the small
	/// colour shifts between rasterisers while catching any visible change.
	/// </summary>
	public double Threshold { get; init; } = 0.1;

	/// <summary>
	/// Counts anti-aliasing pixels as differences. By default they are left out: font and edge rendering differ between
	/// operating systems and graphics stacks mostly in those pixels.
	/// </summary>
	public bool IncludeAntiAliasing { get; init; }

	/// <summary>How many pixels may differ and the image still match. Default 0.</summary>
	public int MaxDifferentPixels { get; init; }

	/// <summary>
	/// The share of pixels, from 0 to 1, that may differ and the image still match. Default 0. When both this and
	/// <see cref="MaxDifferentPixels"/> are set, the larger allowance applies.
	/// </summary>
	public double MaxDifferentRatio { get; init; }

	internal void Validate()
	{
		if (double.IsNaN(Threshold) || Threshold is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(Threshold), Threshold, "Threshold must be between 0 and 1.");
		if (MaxDifferentPixels < 0)
			throw new ArgumentOutOfRangeException(nameof(MaxDifferentPixels), MaxDifferentPixels, "MaxDifferentPixels cannot be negative.");
		if (double.IsNaN(MaxDifferentRatio) || MaxDifferentRatio is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(MaxDifferentRatio), MaxDifferentRatio, "MaxDifferentRatio must be between 0 and 1.");
	}

	/// <summary>How many differing pixels an image of <paramref name="pixels"/> pixels is allowed.</summary>
	internal long Allowed(long pixels) => Math.Max(MaxDifferentPixels, (long)Math.Floor(MaxDifferentRatio * pixels));
}
