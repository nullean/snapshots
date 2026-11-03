using System.IO.Abstractions;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nullean.ScopedFileSystem;

namespace Nullean.Snapshots.Cli;

/// <summary>Exit codes, as documented in the README.</summary>
internal static class ExitCode
{
	public const int Ok = 0;
	public const int Pending = 1;
	public const int Usage = 2;
	public const int Io = 3;
}

/// <summary>
/// What each <c>snap</c> command does, over an <see cref="IFileSystem"/> and two writers so tests can run it in
/// process. Reads and writes are scoped to <paramref name="root"/>.
/// </summary>
internal sealed class SnapCli(IFileSystem fileSystem, string root, TextWriter output, TextWriter error)
{
	private static readonly string[] AllowedHiddenFolders = [".snapshots", ".artifacts"];

	private readonly string _root = fileSystem.Path.TrimEndingDirectorySeparator(fileSystem.Path.GetFullPath(root));

	public int Status(bool json) => Run(pending =>
	{
		if (json)
		{
			output.WriteLine(Json(pending));
			return pending.Count == 0 ? ExitCode.Ok : ExitCode.Pending;
		}

		if (pending.Count == 0)
		{
			output.WriteLine("No pending snapshots.");
			return ExitCode.Ok;
		}

		output.WriteLine($"{pending.Count} pending snapshot(s):");
		var width = pending.Max(p => p.Key.Length);
		foreach (var p in pending)
			output.WriteLine($"  {(p.IsNew ? "new    " : "changed")}  {p.Key.PadRight(width)}  .{p.Extension}");
		output.WriteLine("Review with 'snap diff <key>', then 'snap accept <key>' or 'snap reject <key>' (or --all).");
		return ExitCode.Pending;
	});

	public int Diff(string pattern) => Run((pending, fs) =>
	{
		if (Select(pending, [pattern], all: false) is not { } selected)
			return ExitCode.Usage;
		if (selected.Count > 1)
		{
			error.WriteLine($"'{pattern}' matches {selected.Count} snapshots; diff takes one:");
			foreach (var p in selected)
				error.WriteLine("  " + p.Key);
			return ExitCode.Usage;
		}

		WriteDiff(fs, selected[0], color: false);
		return ExitCode.Ok;
	});

	public int Accept(IReadOnlyList<string> patterns, bool all) => Run((pending, fs) =>
	{
		if (Select(pending, patterns, all) is not { } selected)
			return ExitCode.Usage;
		foreach (var snapshot in selected)
			AcceptOne(fs, snapshot);
		return ExitCode.Ok;
	});

	public int Reject(IReadOnlyList<string> patterns, bool all) => Run((pending, fs) =>
	{
		if (Select(pending, patterns, all) is not { } selected)
			return ExitCode.Usage;
		foreach (var snapshot in selected)
			RejectOne(fs, snapshot);
		return ExitCode.Ok;
	});

	/// <summary>
	/// Walks through every pending snapshot: its diff, then accept, reject, skip or quit. Exits 1 when snapshots are
	/// left pending, 0 otherwise.
	/// </summary>
	public int Review(ISnapPrompt prompt, bool color) => Run((pending, fs) =>
	{
		if (pending.Count == 0)
		{
			output.WriteLine("No pending snapshots.");
			return ExitCode.Ok;
		}

		int accepted = 0, rejected = 0, left = pending.Count;
		for (var i = 0; i < pending.Count; i++)
		{
			var snapshot = pending[i];
			output.WriteLine();
			output.WriteLine($"[{i + 1}/{pending.Count}] {(snapshot.IsNew ? "new" : "changed")}  {snapshot.Key}  .{snapshot.Extension}");
			WriteDiff(fs, snapshot, color);
			var answer = Ask(prompt);
			if (answer == 'q')
				break;
			if (answer == 'a')
			{
				AcceptOne(fs, snapshot);
				accepted++;
				left--;
			}
			else if (answer == 'r')
			{
				RejectOne(fs, snapshot);
				rejected++;
				left--;
			}
		}

		output.WriteLine();
		output.WriteLine($"{accepted} accepted, {rejected} rejected, {left} still pending.");
		return left == 0 ? ExitCode.Ok : ExitCode.Pending;
	});

	/// <summary>
	/// Lists verified files the latest test run did not touch, per the run manifest each project's received folder
	/// keeps. Exits 1 when there are any; <paramref name="delete"/> removes them instead.
	/// </summary>
	public int Orphans(bool json, bool delete) => Run(context =>
	{
		var orphans = new List<(ReceivedRoot Root, string Path, string? Started)>();
		foreach (var root in context.Roots)
		{
			var manifestPath = context.Fs.Path.Combine(root.Directory, RunManifest.FileName);
			if (!context.Fs.File.Exists(manifestPath))
			{
				error.WriteLine($"No run manifest for {root.Project}: run its tests to record which snapshots they use.");
				continue;
			}

			if (!context.Fs.Directory.Exists(root.VerifiedDirectory))
				continue;
			var (touched, started) = RunManifest.Read(context.Fs.File.ReadAllText(manifestPath, SnapshotText.Encoding));
			foreach (var file in context.Fs.Directory.EnumerateFiles(root.VerifiedDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
			{
				var name = context.Fs.Path.GetFileName(file);
				if (!name.Contains(SnapshotPath.VerifiedInfix, StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal))
					continue;
				if (!touched.Contains(SnapshotPath.Relative(context.Fs, root.VerifiedDirectory, file)))
					orphans.Add((root, file, started));
			}
		}

		if (json)
		{
			output.WriteLine(OrphansJson(orphans.Select(o => (o.Root.Project, o.Path))));
			return orphans.Count == 0 || delete ? Deleted(context.Fs, orphans.Select(o => o.Path), delete, quiet: true) : ExitCode.Pending;
		}

		if (orphans.Count == 0)
		{
			output.WriteLine("No orphaned snapshots.");
			return ExitCode.Ok;
		}

		foreach (var group in orphans.GroupBy(o => o.Root.Project))
		{
			var started = group.First().Started;
			output.WriteLine($"{group.Key}: {group.Count()} verified file(s) the latest run{(started is null ? "" : $" (started {started})")} did not use:");
			foreach (var orphan in group)
				output.WriteLine("  " + Relative(orphan.Path));
		}

		if (!delete)
		{
			output.WriteLine("Run the whole suite first: a filtered run leaves out the snapshots it skipped. 'snap orphans --delete' removes these.");
			return ExitCode.Pending;
		}

		return Deleted(context.Fs, orphans.Select(o => o.Path), delete, quiet: false);
	});

	private int Deleted(IFileSystem fs, IEnumerable<string> paths, bool delete, bool quiet)
	{
		if (!delete)
			return ExitCode.Ok;
		foreach (var path in paths)
		{
			fs.File.Delete(path);
			if (!quiet)
				output.WriteLine("deleted  " + Relative(path));
		}

		return ExitCode.Ok;
	}

	private static char Ask(ISnapPrompt prompt)
	{
		while (true)
		{
			var answer = prompt.Ask("[a]ccept, [r]eject, [s]kip, [q]uit? ");
			if (answer is null)
				return 'q';
			if (answer.Value is 'a' or 'r' or 's' or 'q')
				return answer.Value;
		}
	}

	private void WriteDiff(IFileSystem fs, PendingSnapshot snapshot, bool color)
	{
		var bytes = fs.File.ReadAllBytes(snapshot.ReceivedPath);
		if (SnapshotPath.IsBinary(bytes))
		{
			WriteBinary(fs, snapshot, bytes.Length);
			return;
		}

		var received = SnapshotText.Normalize(SnapshotText.Encoding.GetString(bytes));
		var verified = snapshot.IsNew ? string.Empty : SnapshotText.Normalize(fs.File.ReadAllText(snapshot.VerifiedPath, SnapshotText.Encoding));
		output.WriteLine("--- " + (snapshot.IsNew ? "(new snapshot, no verified file)" : Relative(snapshot.VerifiedPath)));
		output.WriteLine("+++ " + Relative(snapshot.ReceivedPath));
		foreach (var hunk in LineDiff.Hunks(verified, received))
		{
			foreach (var line in hunk)
				output.WriteLine(color ? Colored(line) : line);
		}
	}

	/// <summary>A binary snapshot has no line diff: its paths, sizes and attachments (a diff image) instead.</summary>
	private void WriteBinary(IFileSystem fs, PendingSnapshot snapshot, long receivedLength)
	{
		output.WriteLine("--- " + (snapshot.IsNew
			? "(new snapshot, no verified file)"
			: $"{Relative(snapshot.VerifiedPath)}  ({Bytes(fs.FileInfo.New(snapshot.VerifiedPath).Length)})"));
		output.WriteLine($"+++ {Relative(snapshot.ReceivedPath)}  ({Bytes(receivedLength)})");
		output.WriteLine("Binary snapshot: no line diff, open the files to compare.");
		foreach (var attachment in Attachments(fs, snapshot))
			output.WriteLine("    " + Relative(attachment));
	}

	private static string Bytes(long length) => length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes";

	/// <summary>Files written next to the received file for it, such as <c>X.received-diff.png</c>.</summary>
	private static List<string> Attachments(IFileSystem fs, PendingSnapshot snapshot)
	{
		var directory = fs.Path.GetDirectoryName(snapshot.ReceivedPath)!;
		var name = fs.Path.GetFileName(snapshot.ReceivedPath);
		return [.. fs.Directory.EnumerateFiles(directory)
			.Where(f => SnapshotPath.IsAttachmentOf(fs.Path.GetFileName(f), name))
			.Order(StringComparer.Ordinal)];
	}

	// Only the interactive review colours its output, and only on a terminal: messages and other commands stay plain.
	private static string Colored(string line) => line switch
	{
		['-', ..] => "\u001b[31m" + line + "\u001b[0m",
		['+', ..] => "\u001b[32m" + line + "\u001b[0m",
		['@', '@', ..] => "\u001b[36m" + line + "\u001b[0m",
		_ => line,
	};

	/// <summary>
	/// Text is normalised on the way in; a binary snapshot (a NUL in its first 8000 bytes, as git decides) is copied
	/// byte for byte, since normalising line endings would corrupt it.
	/// </summary>
	private void AcceptOne(IFileSystem fs, PendingSnapshot snapshot)
	{
		var bytes = fs.File.ReadAllBytes(snapshot.ReceivedPath);
		if (SnapshotPath.IsBinary(bytes))
			AtomicFile.Write(fs, snapshot.VerifiedPath, bytes.AsMemory());
		else
			AtomicFile.Write(fs, snapshot.VerifiedPath, SnapshotText.Normalize(SnapshotText.Encoding.GetString(bytes)));
		DeleteReceived(fs, snapshot);
		output.WriteLine($"accepted  {snapshot.Key}  -> {Relative(snapshot.VerifiedPath)}");
	}

	private void RejectOne(IFileSystem fs, PendingSnapshot snapshot)
	{
		DeleteReceived(fs, snapshot);
		output.WriteLine($"rejected  {snapshot.Key}");
	}

	private static void DeleteReceived(IFileSystem fs, PendingSnapshot snapshot)
	{
		foreach (var attachment in Attachments(fs, snapshot))
			fs.File.Delete(attachment);
		fs.File.Delete(snapshot.ReceivedPath);
	}

	private int Run(Func<IReadOnlyList<PendingSnapshot>, int> command) => Run((pending, _) => command(pending));

	private int Run(Func<IReadOnlyList<PendingSnapshot>, IFileSystem, int> command) => Run(context => command(context.Pending, context.Fs));

	private sealed record RunContext(IFileSystem Fs, IReadOnlyList<ReceivedRoot> Roots, IReadOnlyList<PendingSnapshot> Pending);

	/// <summary>
	/// Runs a command over the pending snapshots, through a file system scoped to the root: nothing outside it is read
	/// or written. A received folder whose marker points outside the root is skipped with a warning.
	/// </summary>
	private int Run(Func<RunContext, int> command)
	{
		try
		{
			if (!fileSystem.Directory.Exists(_root))
			{
				error.WriteLine($"Root '{_root}' does not exist.");
				return ExitCode.Usage;
			}

			var scoped = Scoped(_root);
			var discovery = new SnapshotDiscovery(scoped);
			var roots = new List<ReceivedRoot>();
			foreach (var found in discovery.FindRoots(_root))
			{
				if (IsSameOrInside(found.VerifiedDirectory, _root))
					roots.Add(found);
				else
					error.WriteLine($"Skipping {Relative(found.Directory)}: its verified folder {found.VerifiedDirectory} is outside {_root}. Pass --root.");
			}

			return command(new RunContext(scoped, roots, discovery.Find(roots)));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
		{
			error.WriteLine("Error: " + e.Message);
			return ExitCode.Io;
		}
	}

	/// <summary>
	/// Picks the snapshots the patterns name: a full key, a unique suffix that starts at a <c>/</c>, or a glob
	/// (<c>*</c> within a segment, <c>**</c> across them) matched against the key or a suffix of it.
	/// </summary>
	internal IReadOnlyList<PendingSnapshot>? Select(IReadOnlyList<PendingSnapshot> pending, IReadOnlyList<string> patterns, bool all)
	{
		if (all)
		{
			if (patterns.Count > 0)
			{
				error.WriteLine("Pass keys or --all, not both.");
				return null;
			}

			return pending;
		}

		if (patterns.Count == 0)
		{
			error.WriteLine("Pass one or more keys, or --all. 'snap status' lists them.");
			return null;
		}

		var selected = new List<PendingSnapshot>();
		foreach (var pattern in patterns)
		{
			var matches = Matches(pending, pattern.Replace('\\', '/').Trim('/'));
			if (matches.Count == 0)
			{
				error.WriteLine($"No pending snapshot matches '{pattern}'. 'snap status' lists them.");
				return null;
			}

			if (!IsGlob(pattern) && matches.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count() > 1)
			{
				error.WriteLine($"'{pattern}' is ambiguous; it matches:");
				foreach (var m in matches)
					error.WriteLine("  " + m.Key);
				return null;
			}

			selected.AddRange(matches.Where(m => !selected.Contains(m)));
		}

		return selected;
	}

	private static List<PendingSnapshot> Matches(IReadOnlyList<PendingSnapshot> pending, string pattern)
	{
		if (IsGlob(pattern))
		{
			var regex = new Regex("(^|/)" + Glob(pattern) + "$", RegexOptions.CultureInvariant);
			return [.. pending.Where(p => regex.IsMatch(p.Key))];
		}

		var exact = pending.Where(p => p.Key == pattern).ToList();
		return exact.Count > 0 ? exact : [.. pending.Where(p => p.Key.EndsWith("/" + pattern, StringComparison.Ordinal))];
	}

	private static bool IsGlob(string pattern) => pattern.AsSpan().IndexOfAny('*', '?') >= 0;

	private static string Glob(string pattern)
	{
		var regex = new StringBuilder();
		for (var i = 0; i < pattern.Length; i++)
		{
			switch (pattern[i])
			{
				case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
					regex.Append(".*");
					i++;
					break;
				case '*':
					regex.Append("[^/]*");
					break;
				case '?':
					regex.Append("[^/]");
					break;
				default:
					regex.Append(Regex.Escape(pattern[i].ToString()));
					break;
			}
		}

		return regex.ToString();
	}

	private string Json(IReadOnlyList<PendingSnapshot> pending)
	{
		using var buffer = new MemoryStream();
		using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
		{
			json.WriteStartArray();
			foreach (var p in pending)
			{
				json.WriteStartObject();
				json.WriteString("key", p.Key);
				json.WriteString("ext", p.Extension);
				json.WriteString("state", p.IsNew ? "new" : "changed");
				if (p.IsNew)
					json.WriteNull("verified");
				else
					json.WriteString("verified", Relative(p.VerifiedPath));
				json.WriteString("received", Relative(p.ReceivedPath));
				json.WriteEndObject();
			}

			json.WriteEndArray();
		}

		return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	private string OrphansJson(IEnumerable<(string Project, string Path)> orphans)
	{
		using var buffer = new MemoryStream();
		using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
		{
			json.WriteStartArray();
			foreach (var (project, path) in orphans)
			{
				json.WriteStartObject();
				json.WriteString("project", project);
				json.WriteString("verified", Relative(path));
				json.WriteEndObject();
			}

			json.WriteEndArray();
		}

		return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	private string Relative(string path) => SnapshotPath.Relative(fileSystem, _root, path);

	private Nullean.ScopedFileSystem.ScopedFileSystem Scoped(string scopeRoot) =>
		new(fileSystem, new ScopedFileSystemOptions(scopeRoot)
		{
			AllowedHiddenFolderNames = AllowedHiddenFolders,
			AllowedHiddenFileNames = [ReceivedMarker.FileName, RunManifest.FileName],
		});

	private bool IsSameOrInside(string path, string root)
	{
		var relative = fileSystem.Path.GetRelativePath(root, path);
		return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !fileSystem.Path.IsPathRooted(relative));
	}
}
