namespace Nullean.Snapshots;

/// <summary>What a snapshot assertion does when the value differs from the verified file.</summary>
public enum SnapshotMode
{
	/// <summary>Fail, and write the value as a received file.</summary>
	Compare,

	/// <summary>Write the value as the verified file and pass (<c>SNAPSHOT_ACCEPT=1</c>, never in CI).</summary>
	Accept,
}

/// <summary>Reads the mode from the environment.</summary>
/// <param name="Mode">The effective mode.</param>
/// <param name="AcceptIgnoredInCi">
/// True when <c>SNAPSHOT_ACCEPT</c> asked to accept but <c>CI</c> is set, so failure messages can say why it had no
/// effect.
/// </param>
public readonly record struct SnapshotEnvironment(SnapshotMode Mode, bool AcceptIgnoredInCi)
{
	/// <summary>The variable that turns on accepting: <c>SNAPSHOT_ACCEPT=1</c> (or <c>true</c>).</summary>
	public const string AcceptVariable = "SNAPSHOT_ACCEPT";

	/// <summary>Set by GitHub Actions, Azure Pipelines, GitLab, Buildkite and most other CI systems.</summary>
	public const string CiVariable = "CI";

	/// <summary>Reads the mode through <paramref name="getVariable"/>.</summary>
	public static SnapshotEnvironment From(Func<string, string?> getVariable)
	{
		ArgumentNullException.ThrowIfNull(getVariable);
		var accept = IsTrue(getVariable(AcceptVariable));
		var ci = IsTrue(getVariable(CiVariable));
		return accept && !ci ? new(SnapshotMode.Accept, false) : new(SnapshotMode.Compare, accept && ci);
	}

	/// <summary>The mode for this process.</summary>
	public static SnapshotEnvironment Current => From(Environment.GetEnvironmentVariable);

	private static bool IsTrue(string? value) => value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
}
