using System.IO.Abstractions;
using Nullean.Snapshots.Images;

namespace Nullean.Snapshots.Browser;

/// <summary>A screenshot that is not the same on the baseline and the target.</summary>
/// <param name="Name">The screenshot's path in its artifacts folder, <c>/</c> separated: <c>nav.png</c>.</param>
/// <param name="Summary">What differs, such as <c>1,234 of 1,024,000 pixels differ (0.12%), 0 allowed</c>.</param>
/// <param name="Regions">Where the pixels differ, as <c>x 10-80, y 4-20 (71x17): 300 pixel(s)</c>, the first few.</param>
/// <param name="DiffPath">The diff image, written next to the target's screenshot as <c>{name}.diff.png</c>; <see langword="null"/> when the images cannot be diffed (a different size, or one side missing).</param>
/// <param name="ZoomPath">The diff cropped to the changed places and enlarged, <c>{name}.zoom.png</c>; <see langword="null"/> when the changes already cover most of the picture.</param>
public sealed record ImageDifference(string Name, string Summary, IReadOnlyList<string> Regions, string? DiffPath, string? ZoomPath = null);

/// <summary>
/// Compares the screenshots a journey wrote against two sites. A journey's <c>screenshot</c> steps write PNGs into its
/// artifacts folder; for a site comparison each site gets a folder of its own, and the pictures with the same name are
/// compared pixel by pixel, as image snapshots are, with a diff image for each pair that differs, a blue box around each place.
/// </summary>
public static class SiteImages
{
	/// <summary>How many regions an <see cref="ImageDifference"/> lists.</summary>
	public const int MaxRegions = 3;

	/// <summary>
	/// The screenshots in <paramref name="baselineDirectory"/> that differ from the one of the same name in
	/// <paramref name="targetDirectory"/>, or are missing from it, and the ones only the target has. A diff image is written
	/// for each pair of the same size that differs. Nothing differs when either folder does not exist.
	/// </summary>
	public static IReadOnlyList<ImageDifference> Compare(IFileSystem fileSystem, string baselineDirectory, string targetDirectory, ImageSnapshotOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(fileSystem);
		if (!fileSystem.Directory.Exists(baselineDirectory) || !fileSystem.Directory.Exists(targetDirectory))
			return [];
		// Two whole pages differ in a few places among many that do not: box the places.
		var format = new ImageSnapshotFormat(options ?? new ImageSnapshotOptions { OutlineRegions = true, ZoomOnChanges = true });
		var baseline = Pictures(fileSystem, baselineDirectory);
		var target = Pictures(fileSystem, targetDirectory);
		var differences = new List<ImageDifference>();
		foreach (var name in baseline.Keys.Union(target.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
		{
			if (!target.TryGetValue(name, out var targetPath))
			{
				differences.Add(new ImageDifference(name, "the target did not take it", [], null));
				continue;
			}

			if (!baseline.TryGetValue(name, out var baselinePath))
			{
				differences.Add(new ImageDifference(name, "the baseline did not take it", [], null));
				continue;
			}

			if (Difference(fileSystem, format, name, baselinePath, targetPath) is { } difference)
				differences.Add(difference);
		}

		return differences;
	}

	private static ImageDifference? Difference(IFileSystem fs, ImageSnapshotFormat format, string name, string baselinePath, string targetPath)
	{
		BinarySnapshotComparison comparison;
		try
		{
			comparison = format.Compare(fs.File.ReadAllBytes(baselinePath), fs.File.ReadAllBytes(targetPath));
		}
		catch (FormatException e)
		{
			return new ImageDifference(name, "not a valid PNG: " + e.Message, [], null);
		}

		if (comparison.Comparison.IsMatch)
			return null;
		string? diffPath = null;
		if (comparison.Attachments.FirstOrDefault(a => a.Suffix == ImageSnapshotFormat.DiffSuffix) is { } diff)
		{
			diffPath = Path.ChangeExtension(targetPath, null) + "." + ImageSnapshotFormat.DiffSuffix;
			fs.File.WriteAllBytes(diffPath, diff.Content.ToArray());
		}

		string? zoomPath = null;
		if (comparison.Attachments.FirstOrDefault(a => a.Suffix == ImageSnapshotFormat.ZoomSuffix) is { } zoom)
		{
			zoomPath = Path.ChangeExtension(targetPath, null) + "." + ImageSnapshotFormat.ZoomSuffix;
			fs.File.WriteAllBytes(zoomPath, zoom.Content.ToArray());
		}

		// A different size has no summary of its own: its one difference line says so.
		var regions = comparison.Comparison.Differences.Select(d => string.Join(' ', d)).ToList();
		var summary = comparison.Comparison.Summary ?? (regions.Count > 0 ? regions[0] : "the pictures differ");
		return new ImageDifference(name, summary, comparison.Comparison.Summary is null ? [] : [.. regions.Take(MaxRegions)], diffPath, zoomPath);
	}

	/// <summary>The PNGs under a folder by their path in it, leaving out diff images.</summary>
	private static Dictionary<string, string> Pictures(IFileSystem fs, string directory) =>
		fs.Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories)
			.Where(f => !f.EndsWith("." + ImageSnapshotFormat.DiffSuffix, StringComparison.OrdinalIgnoreCase) && !f.EndsWith("." + ImageSnapshotFormat.ZoomSuffix, StringComparison.OrdinalIgnoreCase))
			.ToDictionary(f => fs.Path.GetRelativePath(directory, f).Replace('\\', '/'), f => f, StringComparer.Ordinal);
}
