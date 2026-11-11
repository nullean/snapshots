using System.IO.Abstractions;
using Nullean.ScopedFileSystem;

namespace Nullean.Snapshots.Differences;

/// <summary>The rules files a run loaded, or was told about.</summary>
/// <param name="Path">The full path.</param>
/// <param name="Origin">How it was reached.</param>
/// <param name="Exists">Whether it exists; an <c>include?</c> may not.</param>
/// <param name="IncludedFrom">The include line that named it; <see langword="null"/> for the sidecar.</param>
public sealed record RuleFileInfo(string Path, RuleOrigin Origin, bool Exists, RuleLocation? IncludedFrom);

/// <summary>Thrown when rules cannot be read or do not make sense. The message lists every problem as <c>file:line: message</c>.</summary>
public sealed class ExpectedDifferencesException(IReadOnlyList<string> errors) : FormatException(string.Join('\n', errors))
{
	/// <summary>Every problem, one per entry.</summary>
	public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>The rules of one run, and the files they came from.</summary>
public sealed class RuleSet
{
	internal RuleSet(IReadOnlyList<DifferenceRule> rules, IReadOnlyList<RuleFileInfo> files, IFileSystem scoped)
	{
		Rules = rules;
		Files = files;
		Scoped = scoped;
	}

	/// <summary>No rules.</summary>
	public static RuleSet Empty { get; } = new([], [], new System.IO.Abstractions.FileSystem());

	/// <summary>Every rule, the sidecar's first, then each include's in the order they were read.</summary>
	public IReadOnlyList<DifferenceRule> Rules { get; }

	/// <summary>Every file read or named, whether it exists or not.</summary>
	public IReadOnlyList<RuleFileInfo> Files { get; }

	internal IFileSystem Scoped { get; }
}

/// <summary>
/// Loads <c>.differences</c> files. The sidecar of a journey or <c>.http</c> file is the one next to it; it may
/// <c>include</c> others, which must exist, or <c>include?</c> ones that need not. Everything is read through a file
/// system scoped to the repository, so an include cannot leave it, and symbolic links are refused.
/// </summary>
public static class RuleLoader
{
	/// <summary>The extension of a rules file, with its dot.</summary>
	public const string Extension = ".differences";

	/// <summary>
	/// The name of a rules file that applies to every journey and <c>.http</c> file in its folder and below: a hidden file,
	/// <c>.differences</c>, in any folder from the file's own up to the repository root.
	/// </summary>
	public const string FolderFile = ".differences";

	private const int MaxDepth = 16;

	/// <summary>The rules file next to <paramref name="file"/>: <c>shop.journey</c> has <c>shop.differences</c>.</summary>
	public static string SidecarPath(string file) => System.IO.Path.ChangeExtension(file, Extension);

	/// <summary>The nearest folder at or above <paramref name="startDirectory"/> that holds <c>.git</c>; <paramref name="startDirectory"/> when there is none.</summary>
	public static string FindRepositoryRoot(IFileSystem fs, string startDirectory)
	{
		ArgumentNullException.ThrowIfNull(fs);
		for (var current = fs.Path.GetFullPath(startDirectory); !string.IsNullOrEmpty(current); current = fs.Path.GetDirectoryName(current))
		{
			var git = fs.Path.Combine(current, ".git");
			if (fs.Directory.Exists(git) || fs.File.Exists(git))
				return current;
		}

		return fs.Path.GetFullPath(startDirectory);
	}

	/// <summary>
	/// Loads the <see cref="FolderFile"/>s from the repository root down to the sidecar's folder (the nearest last), then
	/// <paramref name="sidecarPath"/> if it exists, and what each includes. A folder file's rules apply to many files, so,
	/// like an <c>include</c>'s, one file not needing a rule does not make it stale. A <c>//path</c> in an include is
	/// relative to <paramref name="repositoryRoot"/>, any other path to the file that includes it.
	/// </summary>
	/// <exception cref="ExpectedDifferencesException">A file cannot be read, is outside the repository or a link, is missing (for <c>include</c>), includes itself, or has a line that is not a rule.</exception>
	public static RuleSet Load(IFileSystem fs, string repositoryRoot, string sidecarPath)
	{
		ArgumentNullException.ThrowIfNull(fs);
		var root = fs.Path.TrimEndingDirectorySeparator(fs.Path.GetFullPath(repositoryRoot));
		var scoped = new ScopedFileSystem.ScopedFileSystem(fs, new ScopedFileSystemOptions(root) { AllowedHiddenFileNames = [FolderFile] });
		var errors = new List<string>();
		var rules = new List<DifferenceRule>();
		var files = new List<RuleFileInfo>();
		var folders = new List<string>();
		for (var folder = fs.Path.GetDirectoryName(fs.Path.GetFullPath(sidecarPath)); folder is not null && IsInside(fs, root, folder); folder = fs.Path.GetDirectoryName(folder))
			folders.Insert(0, folder);
		foreach (var folder in folders)
			Visit(fs, scoped, root, fs.Path.Combine(folder, FolderFile), RuleOrigin.Shared, null, optional: true, [], rules, files, errors);
		// A folder without a rules file is the usual case, not something to list.
		files.RemoveAll(f => !f.Exists);
		Visit(fs, scoped, root, sidecarPath, RuleOrigin.Sidecar, null, optional: true, [], rules, files, errors);
		if (errors.Count > 0)
			throw new ExpectedDifferencesException(errors);
		return new RuleSet(rules, files, scoped);
	}

	private static void Visit(
		IFileSystem raw, IFileSystem fs, string root, string path, RuleOrigin origin, RuleLocation? from, bool optional,
		List<string> stack, List<DifferenceRule> rules, List<RuleFileInfo> files, List<string> errors
	)
	{
		var full = fs.Path.GetFullPath(path);
		var at = from?.ToString() ?? full;
		if (stack.Contains(full, Comparer))
		{
			errors.Add($"{at}: includes loop: {string.Join(" -> ", stack.Append(full))}");
			return;
		}

		if (stack.Count >= MaxDepth)
		{
			errors.Add($"{at}: includes nest more than {MaxDepth} deep.");
			return;
		}

		if (!IsInside(fs, root, full))
		{
			errors.Add($"{at}: {full} is outside the repository ({root}); an include cannot leave it.");
			return;
		}

		bool exists;
		string text = string.Empty;
		try
		{
			exists = fs.File.Exists(full);
			if (exists)
				text = fs.File.ReadAllText(full);
			else if (raw.File.Exists(full))
			{
				// The scoped file system says no to a file that is there: a symbolic link, or a hidden folder on the way.
				errors.Add($"{at}: {full} is refused: includes may not go through symbolic links or hidden folders.");
				return;
			}
		}
		catch (Exception e) when (e is ScopedFileSystemException or IOException or UnauthorizedAccessException)
		{
			errors.Add($"{at}: {full} cannot be read: {e.Message}");
			return;
		}

		files.Add(new RuleFileInfo(full, origin, exists, from));
		if (!exists)
		{
			if (!optional)
				errors.Add($"{at}: {full} does not exist; use 'include?' for a file that may be missing.");
			return;
		}

		var parsed = RuleFileParser.Parse(text, full, origin, errors);
		rules.AddRange(parsed.Rules);
		foreach (var include in parsed.Includes)
		{
			var target = include.Path.StartsWith("//", StringComparison.Ordinal)
				? fs.Path.Combine(root, include.Path[2..].Replace('/', fs.Path.DirectorySeparatorChar))
				: fs.Path.Combine(fs.Path.GetDirectoryName(full) ?? root, include.Path.Replace('/', fs.Path.DirectorySeparatorChar));
			Visit(raw, fs, root, target, include.Optional ? RuleOrigin.Optional : RuleOrigin.Shared, include.Where, include.Optional, [.. stack, full], rules, files, errors);
		}
	}

	private static bool IsInside(IFileSystem fs, string root, string full) =>
		Comparer.Equals(full, root) || full.StartsWith(root + fs.Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	internal static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
