using System.Text.RegularExpressions;

namespace Nullean.Snapshots;

/// <summary>Per-call snapshot options.</summary>
public sealed class SnapshotOptions
{
	private readonly List<Func<string, string>> _scrubbers = [];

	/// <summary>Scrubbers applied to this call's value, after the global ones from <see cref="SnapshotDefaults"/>.</summary>
	public IReadOnlyList<Func<string, string>> Scrubbers => _scrubbers;

	/// <summary>Skip the scrubbers registered in <see cref="SnapshotDefaults"/> for this call.</summary>
	public bool IgnoreDefaultScrubbers { get; set; }

	/// <summary>
	/// Leaves the test class's constructor arguments out of the file name, so a fixture injected into the class does not add a
	/// hash. <see langword="null"/> uses <see cref="SnapshotDefaults.IgnoreClassArguments"/>.
	/// </summary>
	public bool? IgnoreClassArguments { get; set; }

	/// <summary>Adds a scrubber for this call.</summary>
	public SnapshotOptions Scrub(Func<string, string> scrubber)
	{
		ArgumentNullException.ThrowIfNull(scrubber);
		_scrubbers.Add(scrubber);
		return this;
	}

	/// <summary>Adds a scrubber for this call that replaces every match of <paramref name="pattern"/>.</summary>
	public SnapshotOptions Scrub(Regex pattern, string replacement) =>
		Scrub(Nullean.Snapshots.Scrubbers.Regex(pattern, replacement));
}
