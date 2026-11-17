using System.Text.RegularExpressions;

namespace Nullean.Snapshots;

/// <summary>Per-call snapshot options.</summary>
public sealed class SnapshotOptions
{
	private readonly List<Func<string, string>> _scrubbers = [];

	/// <summary>Scrubbers applied to this call's value, after the global ones from <see cref="SnapshotDefaults"/>.</summary>
	public IReadOnlyList<Func<string, string>> Scrubbers => _scrubbers;

	private readonly List<string> _forbidden = [];

	internal IReadOnlyList<string> Forbidden => _forbidden;

	/// <summary>Skip the scrubbers registered in <see cref="SnapshotDefaults"/> for this call.</summary>
	public bool IgnoreDefaultScrubbers { get; set; }

	/// <summary>
	/// Names the snapshot file after the call's <c>name</c> alone, leaving out the hash of the test's arguments, so the cases of one
	/// <c>[Arguments]</c> test get readable files: <c>{Method}.{name}</c> instead of <c>{Method}.{name}.{hash}</c>. The call must pass a name,
	/// and two cases with the same name fail as two assertions resolving to the same snapshot. Off by default, which keeps existing file names.
	/// </summary>
	public bool NameReplacesArguments { get; set; }

	/// <summary>Adds a scrubber for this call.</summary>
	public SnapshotOptions Scrub(Func<string, string> scrubber)
	{
		ArgumentNullException.ThrowIfNull(scrubber);
		_scrubbers.Add(scrubber);
		return this;
	}

	/// <summary>Adds a scrubber for this call that replaces every match of <paramref name="pattern"/>.</summary>
	public SnapshotOptions Scrub(Regex pattern, string replacement) => Scrub(Nullean.Snapshots.Scrubbers.Regex(pattern, replacement));

	/// <summary>
	/// Fails this call when the snapshot, after scrubbing, or its verified file contains any of <paramref name="values"/>, such as secrets or the
	/// names of private repositories. Matching is ordinal. The message gives the line numbers and never the values, and no received file is written,
	/// so a forbidden value does not land on disk. Accepting does not get around it. Empty values are ignored; see <see cref="SnapshotDefaults.Forbid"/> for every snapshot.
	/// </summary>
	public SnapshotOptions Forbid(IEnumerable<string> values)
	{
		ArgumentNullException.ThrowIfNull(values);
		_forbidden.AddRange(values.Where(v => !string.IsNullOrEmpty(v)));
		return this;
	}
}
