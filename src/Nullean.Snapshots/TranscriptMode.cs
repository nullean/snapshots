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
}
