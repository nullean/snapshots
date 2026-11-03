using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Reflection;

namespace Nullean.Snapshots;

/// <summary>The test project a snapshot belongs to, and where its verified and received files go.</summary>
/// <param name="Name">The project name, the first segment of every key.</param>
/// <param name="Directory">The project directory.</param>
/// <param name="SnapshotsDirectory">Where verified files live (<c>SnapshotsDirectory</c>).</param>
/// <param name="ReceivedDirectory">Where received files go (<c>SnapshotsReceivedDirectory</c>).</param>
/// <param name="RepositoryRoot">Paths in messages are relative to this: the nearest folder holding <c>.git</c>.</param>
internal sealed record SnapshotProject(
	string Name,
	string Directory,
	string SnapshotsDirectory,
	string ReceivedDirectory,
	string RepositoryRoot
)
{
	internal const string MetadataPrefix = "Nullean.Snapshots.";
	internal const string ProjectDirectoryKey = MetadataPrefix + "ProjectDirectory";
	internal const string ProjectNameKey = MetadataPrefix + "ProjectName";
	internal const string SnapshotsDirectoryKey = MetadataPrefix + "SnapshotsDirectory";
	internal const string ReceivedDirectoryKey = MetadataPrefix + "ReceivedDirectory";

	/// <summary>
	/// The project's directory relative to <see cref="RepositoryRoot"/>, with <c>/</c> separators; <c>.</c> when they
	/// are the same.
	/// </summary>
	public string RelativeDirectory(IFileSystem fs) => SnapshotPath.Relative(fs, RepositoryRoot, Directory);
}

/// <summary>Finds the <see cref="SnapshotProject"/> for a call, from assembly metadata or by walking up to a csproj.</summary>
internal sealed class SnapshotProjectResolver(IFileSystem fs)
{
	private readonly ConcurrentDictionary<Assembly, SnapshotProject?> _byAssembly = new();
	private readonly ConcurrentDictionary<string, SnapshotProject> _byDirectory = new(StringComparer.Ordinal);

	public SnapshotProject Resolve(Assembly? testAssembly, string callerFilePath)
	{
		if (testAssembly is not null && _byAssembly.GetOrAdd(testAssembly, FromMetadata) is { } fromMetadata)
			return fromMetadata;
		return FromCallerFile(callerFilePath);
	}

	/// <summary>Reads the <c>[AssemblyMetadata]</c> that <c>Nullean.Snapshots.targets</c> writes.</summary>
	internal SnapshotProject? FromMetadata(Assembly assembly)
	{
		var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.Where(a => a.Key.StartsWith(SnapshotProject.MetadataPrefix, StringComparison.Ordinal))
			.GroupBy(a => a.Key, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
		return FromValues(metadata);
	}

	internal SnapshotProject? FromValues(IReadOnlyDictionary<string, string?> metadata)
	{
		if (!metadata.TryGetValue(SnapshotProject.ProjectDirectoryKey, out var directory) || string.IsNullOrWhiteSpace(directory))
			return null;
		directory = Full(directory);
		var name = metadata.TryGetValue(SnapshotProject.ProjectNameKey, out var n) && !string.IsNullOrWhiteSpace(n)
			? n
			: fs.Path.GetFileName(directory);
		var snapshots = metadata.TryGetValue(SnapshotProject.SnapshotsDirectoryKey, out var s) && !string.IsNullOrWhiteSpace(s)
			? Full(s)
			: fs.Path.Combine(directory, ".snapshots");
		var received = metadata.TryGetValue(SnapshotProject.ReceivedDirectoryKey, out var r) && !string.IsNullOrWhiteSpace(r)
			? Full(r)
			: DefaultReceived(directory);
		return new SnapshotProject(name, directory, snapshots, received, RepositoryRoot(directory));
	}

	/// <summary>
	/// The fallback for an assembly built without the targets: the nearest <c>*.csproj</c> above the test file, with
	/// the default folders.
	/// </summary>
	internal SnapshotProject FromCallerFile(string callerFilePath)
	{
		var start = fs.Path.GetDirectoryName(callerFilePath);
		if (string.IsNullOrEmpty(start) || !fs.Path.IsPathRooted(callerFilePath) || SnapshotPath.IsDeterministic(callerFilePath))
			throw new InvalidOperationException(
				$"Cannot find the test project for '{callerFilePath}'. Reference Nullean.Snapshots from the test project " +
				"so its build targets record the project directory, or build without deterministic source paths.");

		return _byDirectory.GetOrAdd(start, dir =>
		{
			for (var current = dir; !string.IsNullOrEmpty(current); current = fs.Path.GetDirectoryName(current))
			{
				if (!fs.Directory.Exists(current))
					continue;
				var project = fs.Directory.EnumerateFiles(current, "*.csproj").Order(StringComparer.Ordinal).FirstOrDefault();
				if (project is null)
					continue;
				var full = Full(current);
				return new SnapshotProject(
					fs.Path.GetFileNameWithoutExtension(project),
					full,
					fs.Path.Combine(full, ".snapshots"),
					DefaultReceived(full),
					RepositoryRoot(full));
			}

			throw new InvalidOperationException(
				$"Cannot find the test project for '{callerFilePath}': no *.csproj in any folder above it.");
		});
	}

	private string DefaultReceived(string projectDirectory) =>
		fs.Path.Combine(projectDirectory, "obj", "snapshots", "received");

	private string RepositoryRoot(string projectDirectory)
	{
		for (var current = projectDirectory; !string.IsNullOrEmpty(current); current = fs.Path.GetDirectoryName(current))
		{
			var git = fs.Path.Combine(current, ".git");
			if (fs.Directory.Exists(git) || fs.File.Exists(git))
				return current;
		}

		return projectDirectory;
	}

	private string Full(string path) => fs.Path.TrimEndingDirectorySeparator(fs.Path.GetFullPath(path));
}
