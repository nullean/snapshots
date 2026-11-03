using System.Text;

namespace Nullean.Snapshots;

/// <summary>The on-disk form of a snapshot: UTF-8 without a BOM, LF line endings, one trailing newline.</summary>
public static class SnapshotText
{
	private const char ByteOrderMark = '\uFEFF';

	/// <summary>The encoding every snapshot file is written with.</summary>
	public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	/// <summary>
	/// Normalises text to the on-disk form: strips leading BOMs, turns CRLF and lone CR into LF, and ends the text
	/// with exactly one newline. Empty text stays empty.
	/// </summary>
	public static string Normalize(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		text = text.TrimStart(ByteOrderMark);
		if (text.Contains('\r', StringComparison.Ordinal))
			text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
		var end = text.Length;
		while (end > 0 && text[end - 1] == '\n')
			end--;
		return end == 0 ? string.Empty : string.Concat(text.AsSpan(0, end), "\n");
	}
}
