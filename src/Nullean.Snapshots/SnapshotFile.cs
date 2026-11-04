using System.IO.Abstractions;
using System.Reflection;

namespace Nullean.Snapshots;

/// <summary>
/// A source file a snapshot belongs to, such as a journey, as a test data source yields it.
/// </summary>
/// <param name="Path">The file's path in the test project, with <c>/</c> separators: <c>journeys/basket/empty.journey</c>.</param>
/// <param name="Name">The name tests show: the path below the searched folder, without the extension (<c>basket/empty</c>).</param>
public sealed record SnapshotFile(string Path, string Name)
{
	private static readonly SnapshotProjectResolver Resolver = new(new FileSystem());

	/// <summary>The <see cref="Name"/>, so a data-driven test reads <c>Run(basket/empty)</c>.</summary>
	public override string ToString() => Name;

	/// <summary>
	/// The files matching <paramref name="pattern"/> (<c>*.journey</c>) under <paramref name="folder"/> of the test
	/// project <paramref name="testAssembly"/> belongs to, sorted by path. Build output, <c>node_modules</c> and dot
	/// folders such as <c>.snapshots</c> are skipped.
	/// </summary>
	/// <exception cref="InvalidOperationException">The assembly carries no Nullean.Snapshots project metadata.</exception>
	public static IReadOnlyList<SnapshotFile> Find(Assembly testAssembly, string folder, string pattern)
	{
		ArgumentNullException.ThrowIfNull(testAssembly);
		var project = Resolver.FromAssembly(testAssembly)
			?? throw new InvalidOperationException(
				$"{testAssembly.GetName().Name} has no Nullean.Snapshots project metadata; reference Nullean.Snapshots from the test project.");
		return Find(new FileSystem(), project.Directory, folder, pattern);
	}

	internal static IReadOnlyList<SnapshotFile> Find(IFileSystem fs, string projectDirectory, string folder, string pattern)
	{
		ArgumentNullException.ThrowIfNull(folder);
		ArgumentException.ThrowIfNullOrEmpty(pattern);
		var trimmed = folder.Replace('\\', '/').Trim('/');
		var root = trimmed.Length == 0 ? projectDirectory : fs.Path.Combine([projectDirectory, .. trimmed.Split('/')]);
		if (!fs.Directory.Exists(root))
			return [];
		return [.. fs.Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
			.Select(f => (Within: fs.Path.GetRelativePath(root, f).Replace('\\', '/'), File: f))
			.Where(f => !f.Within.Split('/')[..^1].Any(part => part is "bin" or "obj" or "node_modules" || part.StartsWith('.')))
			.Select(f => new SnapshotFile(
				fs.Path.GetRelativePath(projectDirectory, f.File).Replace('\\', '/'),
				f.Within[..^fs.Path.GetExtension(f.Within).Length]))
			.OrderBy(f => f.Path, StringComparer.Ordinal)];
	}
}
