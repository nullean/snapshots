using System.Globalization;
using System.Text;

namespace Nullean.Snapshots;

/// <summary>The caps that keep a failure message readable.</summary>
public static class SnapshotMessageLimits
{
	/// <summary>At most this many differences are listed.</summary>
	public const int MaxDifferences = 20;

	/// <summary>At most this many lines describe them.</summary>
	public const int MaxLines = 80;
}

/// <summary>Everything a failure message is built from. Paths are repository-relative with <c>/</c> separators.</summary>
internal sealed record SnapshotFailure(
	string FormatName,
	string VerifiedPath,
	string ReceivedPath,
	string Key,
	SnapshotComparison? Comparison,
	string? Because,
	string ProjectPath,
	string? TreeNodeFilter,
	bool AcceptIgnoredInCi
)
{
	/// <summary>True when there was no verified file to compare with.</summary>
	public bool IsNew => Comparison is null;

	/// <summary>
	/// Renders the message. Content is only ever appended, never used as a format string, so braces in a snapshot are
	/// safe.
	/// </summary>
	public string Render()
	{
		var message = new StringBuilder();
		var because = Because is { Length: > 0 } b ? " " + b : string.Empty;
		if (Comparison is null)
			message.Append(FormatName).Append(" snapshot is new").Append(because).Append(": no verified file at ").Append(VerifiedPath).Append('\n');
		else
		{
			message.Append(FormatName).Append(" did not match snapshot").Append(because).Append(": ").Append(VerifiedPath).Append('\n');
			AppendDifferences(message, Comparison.Differences);
		}

		message.Append("Received: ").Append(ReceivedPath).Append('\n');
		message.Append("Accept:   snap accept ").Append(Key).Append("   (or: dnx Nullean.Snapshots.Cli accept ").Append(Key).Append(")\n");
		if (TreeNodeFilter is not null)
		{
			message.Append("   or:    ").Append(SnapshotEnvironment.AcceptVariable).Append("=1 dotnet test --project ").Append(ProjectPath)
				.Append(" --treenode-filter \"").Append(TreeNodeFilter).Append("\"\n");
		}

		if (AcceptIgnoredInCi)
		{
			message.Append(SnapshotEnvironment.AcceptVariable).Append(" is ignored because ").Append(SnapshotEnvironment.CiVariable)
				.Append(" is set: snapshots are never accepted in CI.\n");
		}

		return message.ToString().TrimEnd('\n');
	}

	/// <summary>
	/// Lists differences up to <see cref="SnapshotMessageLimits.MaxDifferences"/> and
	/// <see cref="SnapshotMessageLimits.MaxLines"/>. A difference that does not fit in the remaining lines is left out
	/// whole, except a first difference that is longer than the cap on its own: that one is cut, so the message still
	/// shows something. Either way it counts as not shown.
	/// </summary>
	internal static void AppendDifferences(StringBuilder message, IReadOnlyList<IReadOnlyList<string>> differences)
	{
		message.Append(differences.Count.ToString(CultureInfo.InvariantCulture)).Append(" difference(s):\n");
		var lines = 0;
		var shown = 0;
		foreach (var difference in differences)
		{
			if (shown == SnapshotMessageLimits.MaxDifferences)
				break;
			if (lines + difference.Count > SnapshotMessageLimits.MaxLines)
			{
				if (shown == 0)
				{
					foreach (var line in difference.Take(SnapshotMessageLimits.MaxLines))
						message.Append(line).Append('\n');
				}

				break;
			}

			foreach (var line in difference)
				message.Append(line).Append('\n');
			lines += difference.Count;
			shown++;
		}

		var more = differences.Count - shown;
		if (more > 0)
			message.Append("(+").Append(more.ToString(CultureInfo.InvariantCulture)).Append(" more, see received)\n");
	}
}
