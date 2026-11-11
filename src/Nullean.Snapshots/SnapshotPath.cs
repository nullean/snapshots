using System.Globalization;
using System.IO.Abstractions;
using System.Text;

namespace Nullean.Snapshots;

/// <summary>Key and path arithmetic shared by the library and the CLI.</summary>
internal static class SnapshotPath
{
	public const string VerifiedInfix = ".verified.";
	public const string ReceivedInfix = ".received.";

	/// <summary>
	/// Attachments are <c>{name}.received-{suffix}</c>. The dash keeps them out of <see cref="ReceivedInfix"/>, so
	/// they are never listed as pending.
	/// </summary>
	public const string AttachmentInfix = ".received-";

	/// <summary>
	/// True for a path rewritten by deterministic builds (<c>/_/</c>, <c>/_1/</c>, …), which has no directory on disk.
	/// </summary>
	public static bool IsDeterministic(string path)
	{
		if (path.Length < 3 || path[0] != '/' || path[1] != '_')
			return false;
		var i = 2;
		while (i < path.Length && char.IsAsciiDigit(path[i]))
			i++;
		return i < path.Length && path[i] == '/';
	}

	/// <summary>
	/// The folder of <paramref name="callerFilePath"/> relative to <paramref name="projectDirectory"/>, with <c>/</c>
	/// separators and empty for the project root. A deterministic path (<c>/_/tests/Acme.Tests/Rendering/X.cs</c>) is
	/// mapped back by matching its leading folders against the end of the project directory. A file outside the
	/// project gets the project root.
	/// </summary>
	public static string RelativeTestDirectory(IFileSystem fs, string projectDirectory, string callerFilePath)
	{
		var projectSegments = Segments(projectDirectory);
		string[] fileSegments;
		if (IsDeterministic(callerFilePath))
		{
			var rest = Segments(callerFilePath).Skip(1).ToArray();
			fileSegments = [];
			for (var k = Math.Min(rest.Length - 1, projectSegments.Length); k > 0; k--)
			{
				if (!rest.AsSpan(0, k).SequenceEqual(projectSegments.AsSpan(projectSegments.Length - k), PathComparer))
					continue;
				fileSegments = rest[k..];
				break;
			}
		}
		else
		{
			var full = Segments(fs.Path.GetFullPath(callerFilePath));
			fileSegments = full.Length > projectSegments.Length && full.Take(projectSegments.Length).SequenceEqual(projectSegments, PathComparer)
				? full[projectSegments.Length..]
				: [];
		}

		return fileSegments.Length <= 1 ? string.Empty : string.Join('/', fileSegments[..^1]);
	}

	/// <summary><c>{Project}/{dir}/{Class}/{Method}[.{name}][.{hash}]</c>, with empty parts left out.</summary>
	public static string Key(string project, string relativeDirectory, string className, string methodName, string? name, string? argumentsHash)
	{
		var file = new StringBuilder(Sanitize(methodName));
		if (!string.IsNullOrEmpty(name))
			file.Append('.').Append(name);
		if (!string.IsNullOrEmpty(argumentsHash))
			file.Append('.').Append(argumentsHash);
		IEnumerable<string> parts = [Sanitize(project), relativeDirectory, Sanitize(className), file.ToString()];
		return string.Join('/', parts.Where(p => p.Length > 0));
	}

	/// <summary>The key without its leading project segment: the path below the snapshots folder.</summary>
	public static string KeyWithinProject(string key)
	{
		var slash = key.IndexOf('/', StringComparison.Ordinal);
		return slash < 0 ? key : key[(slash + 1)..];
	}

	public static string File(IFileSystem fs, string root, string keyWithinProject, string infix, string extension) =>
		fs.Path.Combine([root, .. (keyWithinProject + infix + extension).Split('/')]);

	/// <summary>
	/// True when <paramref name="fileName"/> is an attachment of the received file <paramref name="receivedName"/>
	/// (<c>X.received.png</c> owns <c>X.received-diff.png</c>). A file that holds <see cref="ReceivedInfix"/> is a
	/// snapshot of its own, never an attachment.
	/// </summary>
	public static bool IsAttachmentOf(string fileName, string receivedName)
	{
		var at = receivedName.LastIndexOf(ReceivedInfix, StringComparison.Ordinal);
		if (at <= 0)
			return false;
		var prefix = string.Concat(receivedName.AsSpan(0, at), AttachmentInfix);
		return fileName.Length > prefix.Length && fileName.StartsWith(prefix, StringComparison.Ordinal) &&
			!fileName.Contains(ReceivedInfix, StringComparison.Ordinal) && !fileName.EndsWith(".tmp", StringComparison.Ordinal);
	}

	/// <summary>
	/// True when the file looks binary by git's rule: a NUL byte in its first 8000 bytes. Binary snapshots are copied
	/// byte for byte; text ones are normalised.
	/// </summary>
	public static bool IsBinary(ReadOnlySpan<byte> content) => content[..Math.Min(content.Length, 8000)].Contains((byte)0);

	/// <summary>Swaps a received file name for its verified one.</summary>
	public static string ToVerifiedName(string receivedName)
	{
		var at = receivedName.LastIndexOf(ReceivedInfix, StringComparison.Ordinal);
		return at < 0 ? receivedName : string.Concat(receivedName.AsSpan(0, at), VerifiedInfix, receivedName.AsSpan(at + ReceivedInfix.Length));
	}

	/// <summary><paramref name="path"/> relative to <paramref name="root"/>, with <c>/</c> separators.</summary>
	public static string Relative(IFileSystem fs, string root, string path)
	{
		var relative = fs.Path.GetRelativePath(root, path);
		return relative.Replace('\\', '/');
	}

	/// <summary>
	/// A file-name-safe version of <paramref name="value"/>: generic arity markers dropped and anything outside letters,
	/// digits, <c>.</c>, <c>-</c> and <c>_</c> turned into <c>_</c>.
	/// </summary>
	public static string Sanitize(string value)
	{
		var builder = new StringBuilder(value.Length);
		for (var i = 0; i < value.Length; i++)
		{
			var c = value[i];
			if (c == '`')
			{
				while (i + 1 < value.Length && char.IsAsciiDigit(value[i + 1]))
					i++;
				continue;
			}

			builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
		}

		return builder.ToString();
	}

	/// <summary>True when <paramref name="name"/> can be used as a snapshot name as given.</summary>
	public static bool IsValidName(string name) => name.Length > 0 && name.Trim('.').Length == name.Length && Sanitize(name) == name;

	/// <summary>
	/// An eight hex digit FNV-1a hash of the arguments' invariant text, stable across runs, machines and .NET versions.
	/// Empty when there are no arguments.
	/// </summary>
	public static string ArgumentsHash(IReadOnlyList<object?> arguments)
	{
		if (arguments.Count == 0)
			return string.Empty;
		var text = new StringBuilder();
		foreach (var argument in arguments)
		{
			Append(text, argument);
			text.Append('\u001F');
		}

		// A multi-line string argument taken from a source file has that file's line endings, and a Windows checkout turns .cs files into CRLF:
		// the same test would be named for a different hash on each operating system. Only \r\n and a lone \r are normalised (as snapshot content is).
		var hash = 2166136261u;
		foreach (var b in Encoding.UTF8.GetBytes(text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')))
			hash = (hash ^ b) * 16777619u;
		return hash.ToString("x8", CultureInfo.InvariantCulture);
	}

	private static void Append(StringBuilder text, object? value)
	{
		switch (value)
		{
			case null:
				text.Append("null");
				break;
			case string s:
				text.Append('"').Append(s).Append('"');
				break;
			case IFormattable f:
				text.Append(f.ToString(null, CultureInfo.InvariantCulture));
				break;
			case System.Collections.IEnumerable items:
				text.Append('[');
				foreach (var item in items)
				{
					Append(text, item);
					text.Append(',');
				}

				text.Append(']');
				break;
			default:
				text.Append(value);
				break;
		}
	}

	internal static StringComparer PathComparer { get; } = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

	private static string[] Segments(string path) => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
