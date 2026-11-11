namespace Nullean.Snapshots;

/// <summary>Whether a journey or <c>.http</c> run is compared with its verified transcript.</summary>
public enum TranscriptMode
{
	/// <summary>The transcript is compared with the verified snapshot. The default.</summary>
	Compare,

	/// <summary>
	/// Only the run's own assertions count: a step or request that fails fails the test, and the transcript is neither
	/// compared nor written. No verified file is needed. For a live site whose content changes.
	/// </summary>
	Off,

	/// <summary>
	/// The transcript is compared like <see cref="Compare"/>, but written as its shape: every JSON value is replaced by
	/// its kind (<c>"string"</c>, <c>false</c>, <c>1</c>, <c>1.0</c>), arrays collapse to their distinct shapes and a text
	/// body shows only its media type. A property added or dropped fails; a changed value never does. Only <c>.http</c>
	/// runs have a shape; a journey rejects it.
	/// </summary>
	Shape,
}
