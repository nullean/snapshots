using System.IO.Abstractions;

namespace Nullean.Snapshots.Cli;

/// <summary>A received folder and the verified folder its marker points at.</summary>
internal sealed record ReceivedRoot(string Directory, string Project, string VerifiedDirectory);

/// <summary>A received file waiting to be accepted or rejected.</summary>
/// <param name="Key">The snapshot key, <c>{Project}/{dir}/{Class}/{Method}[.{name}][.{hash}]</c>.</param>
/// <param name="Extension">The file extension without a dot.</param>
/// <param name="VerifiedPath">Where accepting writes to: the verified file, or for an inline snapshot the test's source file.</param>
/// <param name="ReceivedPath">The received file.</param>
/// <param name="IsNew">True when there is no verified file yet, or for an inline snapshot no expected value.</param>
/// <param name="Inline">The record of an inline snapshot (<c>.received.inline</c>); null for every other kind.</param>
/// <param name="Stale">
/// True when the received file is older than the start of its project's latest test run: that run did not write it again, and a
/// run that passed the test would have deleted it, so it is left over from an earlier run (changed code, a filtered run).
/// </param>
internal sealed record PendingSnapshot(
	string Key,
	string Extension,
	string VerifiedPath,
	string ReceivedPath,
	bool IsNew,
	InlineRecord? Inline = null,
	bool Stale = false
);

/// <summary>
/// Finds received files without globbing the whole tree: it probes the artifacts folders, then <c>obj/</c> next to
/// each project file. See DECISIONS.md, "CLI discovery".
/// </summary>
internal sealed class SnapshotDiscovery(IFileSystem fs)
{
	private static readonly string[] ArtifactsFolders = ["artifacts", ".artifacts"];
	private static readonly HashSet<string> Skipped = new(["bin", "obj", "node_modules"], StringComparer.OrdinalIgnoreCase);

	public IReadOnlyList<ReceivedRoot> FindRoots(string root)
	{
		var roots = new List<ReceivedRoot>();
		foreach (var artifacts in ArtifactsFolders)
		{
			var received = fs.Path.Combine(root, artifacts, ".snapshots", "received");
			if (!fs.Directory.Exists(received))
				continue;
			foreach (var project in fs.Directory.EnumerateDirectories(received).Order(StringComparer.Ordinal))
			{
				if (Marker(project) is { } marker)
					roots.Add(new ReceivedRoot(project, marker.Project, marker.ResolveVerified(fs, project)));
			}
		}

		foreach (var projectDirectory in ProjectDirectories(root))
		{
			var received = fs.Path.Combine(projectDirectory, "obj", "snapshots", "received");
			if (!fs.Directory.Exists(received))
				continue;
			// The marker is written with the first received file; without it fall back to the default layout.
			var marker = Marker(received);
			var project = marker?.Project ?? fs.Path.GetFileNameWithoutExtension(
				fs.Directory.EnumerateFiles(projectDirectory, "*.csproj").Order(StringComparer.Ordinal).First()
			);
			var verified = marker?.ResolveVerified(fs, received) ?? fs.Path.Combine(projectDirectory, ".snapshots");
			roots.Add(new ReceivedRoot(received, project, verified));
		}

		return roots;
	}

	/// <summary>
	/// The received files under <paramref name="roots"/>. <paramref name="warn"/> hears about files that cannot be used (an
	/// inline record that does not parse), with the file's path and why.
	/// </summary>
	public IReadOnlyList<PendingSnapshot> Find(IReadOnlyList<ReceivedRoot> roots, Action<string, string>? warn = null)
	{
		var pending = new List<PendingSnapshot>();
		foreach (var root in roots)
		{
			var started = RunStarted(root);
			foreach (var file in fs.Directory.EnumerateFiles(root.Directory, "*", SearchOption.AllDirectories))
			{
				var name = fs.Path.GetFileName(file);
				var at = name.LastIndexOf(SnapshotPath.ReceivedInfix, StringComparison.Ordinal);
				if (at <= 0 || name.EndsWith(".tmp", StringComparison.Ordinal))
					continue;
				var extension = name[(at + SnapshotPath.ReceivedInfix.Length)..];
				var relative = SnapshotPath.Relative(fs, root.Directory, file);
				var withinProject = relative[..(relative.Length - name.Length + at)];
				if (extension == "inline")
				{
					try
					{
						var record = InlineRecord.Parse(fs.File.ReadAllText(file, SnapshotText.Encoding));
						var source = fs.Path.GetFullPath(fs.Path.Combine(root.Directory, record.Source.Replace('/', fs.Path.DirectorySeparatorChar)));
						pending.Add(
							new PendingSnapshot(
								root.Project + "/" + withinProject,
								extension,
								source,
								file,
								record.Edit.Expected is null,
								record,
								IsStale(file, started)
							)
						);
					}
					catch (FormatException e)
					{
						warn?.Invoke(file, e.Message);
					}

					continue;
				}

				var verified = SnapshotPath.File(fs, root.VerifiedDirectory, withinProject, SnapshotPath.VerifiedInfix, extension);
				pending.Add(
					new PendingSnapshot(root.Project + "/" + withinProject, extension, verified, file, !fs.File.Exists(verified), Stale: IsStale(file, started))
				);
			}
		}

		return [.. pending.OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Extension, StringComparer.Ordinal)];
	}

	/// <summary>When the latest test run of the project started, from the run manifest its test process wrote; <see langword="null"/> without one.</summary>
	private DateTime? RunStarted(ReceivedRoot root)
	{
		var path = fs.Path.Combine(root.Directory, RunManifest.FileName);
		if (!fs.File.Exists(path))
			return null;
		return RunManifest.Read(fs.File.ReadAllText(path, SnapshotText.Encoding)).Started is { } started && DateTime.TryParse(
			started,
			System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
			out var when
		) ? when : null;
	}

	private bool IsStale(string receivedFile, DateTime? runStarted) => runStarted is { } started && fs.File.GetLastWriteTimeUtc(receivedFile) < started;

	/// <summary>Folders holding a <c>*.csproj</c>, skipping <c>bin</c>, <c>obj</c>, <c>node_modules</c> and dot folders.</summary>
	private IEnumerable<string> ProjectDirectories(string root)
	{
		var stack = new Stack<string>([root]);
		while (stack.Count > 0)
		{
			var directory = stack.Pop();
			if (fs.Directory.EnumerateFiles(directory, "*.csproj").Any())
				yield return directory;
			foreach (var child in fs.Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).Reverse())
			{
				var name = fs.Path.GetFileName(child);
				if (name.StartsWith('.') || Skipped.Contains(name))
					continue;
				stack.Push(child);
			}
		}
	}

	private ReceivedMarker? Marker(string receivedDirectory)
	{
		var path = fs.Path.Combine(receivedDirectory, ReceivedMarker.FileName);
		return fs.File.Exists(path) ? ReceivedMarker.Parse(fs.File.ReadAllText(path, SnapshotText.Encoding)) : null;
	}
}
