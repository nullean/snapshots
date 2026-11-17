using System.Globalization;
using System.Text;

namespace Nullean.Snapshots;

/// <summary>The check behind <see cref="SnapshotOptions.Forbid"/>. Reports line numbers only, never a value.</summary>
internal static class ForbiddenValues
{
	private const int MaxListed = 10;

	public static IReadOnlyList<string> Collect(SnapshotOptions? options)
	{
		var defaults = SnapshotDefaults.Forbidden;
		var own = options?.Forbidden ?? [];
		if (defaults.Count == 0)
			return own;
		return own.Count == 0 ? defaults : [.. defaults, .. own];
	}

	/// <summary>The 1-based numbers of the lines of <paramref name="text"/> that contain any of <paramref name="values"/>. Text is LF-normalised.</summary>
	public static List<int> Lines(string text, IReadOnlyList<string> values)
	{
		var hits = new List<int>();
		var line = 1;
		var start = 0;
		while (start <= text.Length)
		{
			var end = text.IndexOf('\n', start);
			if (end < 0)
				end = text.Length;
			var span = text.AsSpan(start, end - start);
			foreach (var value in values)
			{
				if (span.Contains(value.AsSpan(), StringComparison.Ordinal))
				{
					hits.Add(line);
					break;
				}
			}

			line++;
			start = end + 1;
		}

		return hits;
	}

	public static string Describe(IReadOnlyList<int> lines)
	{
		var text = new StringBuilder();
		text.Append(lines.Count.ToString(CultureInfo.InvariantCulture)).Append(lines.Count == 1 ? " line (" : " lines (");
		text.Append(string.Join(", ", lines.Take(MaxListed).Select(l => l.ToString(CultureInfo.InvariantCulture))));
		if (lines.Count > MaxListed)
			text.Append(", +").Append((lines.Count - MaxListed).ToString(CultureInfo.InvariantCulture)).Append(" more");
		return text.Append(')').ToString();
	}
}
