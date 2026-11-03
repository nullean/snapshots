using System.IO.Abstractions;
using System.Text;

namespace Nullean.Snapshots;

/// <summary>
/// The <c>.snapshots-project</c> file at the top of a received folder. It tells the CLI which project the received
/// files belong to and where their verified files live, so the CLI never has to evaluate MSBuild.
/// </summary>
/// <param name="Project">The project name.</param>
/// <param name="VerifiedDirectory">The verified folder, relative to the marker's folder, with <c>/</c> separators.</param>
internal sealed record ReceivedMarker(string Project, string VerifiedDirectory)
{
	public const string FileName = ".snapshots-project";

	public string Render() => new StringBuilder()
		.Append("project=").Append(Project).Append('\n')
		.Append("verified=").Append(VerifiedDirectory).Append('\n')
		.ToString();

	/// <summary>Parses <c>key=value</c> lines; unknown keys are ignored, and a marker without a project is null.</summary>
	public static ReceivedMarker? Parse(string text)
	{
		string? project = null, verified = null;
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			var eq = line.IndexOf('=', StringComparison.Ordinal);
			if (eq <= 0)
				continue;
			var value = line[(eq + 1)..].Trim();
			switch (line[..eq].Trim())
			{
				case "project":
					project = value;
					break;
				case "verified":
					verified = value;
					break;
			}
		}

		return string.IsNullOrEmpty(project) || string.IsNullOrEmpty(verified) ? null : new ReceivedMarker(project, verified);
	}

	public static ReceivedMarker For(IFileSystem fs, SnapshotProject project) =>
		new(project.Name, SnapshotPath.Relative(fs, project.ReceivedDirectory, project.SnapshotsDirectory));

	/// <summary>The verified folder this marker points at, as a full path.</summary>
	public string ResolveVerified(IFileSystem fs, string receivedDirectory) =>
		fs.Path.GetFullPath(fs.Path.Combine([receivedDirectory, .. VerifiedDirectory.Split('/')]));

	/// <summary>Writes the marker unless an identical one is already there. Safe to call from parallel tests.</summary>
	public void EnsureWritten(IFileSystem fs, string receivedDirectory)
	{
		var path = fs.Path.Combine(receivedDirectory, FileName);
		var content = Render();
		if (fs.File.Exists(path) && ReadOrNull(fs, path) == content)
			return;
		try
		{
			AtomicFile.Write(fs, path, content);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException && ReadOrNull(fs, path) == content)
		{
			// Another writer (another test process) put the same marker there at the same moment. Windows reports a
			// move onto a file being replaced as access denied rather than an IO error.
		}
	}

	private static string? ReadOrNull(IFileSystem fs, string path)
	{
		try
		{
			return fs.File.ReadAllText(path, SnapshotText.Encoding);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}

internal static class AtomicFile
{
	/// <summary>
	/// Writes <paramref name="content"/> to a temporary file next to <paramref name="path"/> and moves it into place, so
	/// a reader never sees half a file. Creates the folder if needed.
	/// </summary>
	public static void Write(IFileSystem fs, string path, string content) =>
		Write(fs, path, content, static (fs, temp, content) => fs.File.WriteAllText(temp, content, SnapshotText.Encoding));

	/// <summary>Writes bytes the same way as <see cref="Write(IFileSystem, string, string)"/>.</summary>
	public static void Write(IFileSystem fs, string path, ReadOnlyMemory<byte> content) =>
		Write(fs, path, content, static (fs, temp, content) =>
		{
			using var stream = fs.File.Create(temp);
			stream.Write(content.Span);
		});

	private static void Write<T>(IFileSystem fs, string path, T content, Action<IFileSystem, string, T> write)
	{
		var directory = fs.Path.GetDirectoryName(path)!;
		fs.Directory.CreateDirectory(directory);
		var temp = fs.Path.Combine(directory, $"{fs.Path.GetFileName(path).TrimStart('.')}.{Guid.NewGuid():N}.tmp");
		try
		{
			write(fs, temp, content);
			fs.File.Move(temp, path, overwrite: true);
		}
		finally
		{
			if (fs.File.Exists(temp))
				fs.File.Delete(temp);
		}
	}
}
