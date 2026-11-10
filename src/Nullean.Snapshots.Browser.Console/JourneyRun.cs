using Nullean.Cdp.Journeys;

namespace Nullean.Snapshots.Browser.Console;

/// <summary>A journey file of a run: its path, and the id it goes by in the output, in <c>--only</c> and in a rerun command.</summary>
/// <param name="Id">What the journey is called in the output, such as <c>navigation/sidebar</c>.</param>
/// <param name="Path">The journey file.</param>
public sealed record JourneyRunFile(string Id, string Path)
{
	/// <summary>Where to open the file, as shown in a failure. The path relative to the working folder when unset.</summary>
	public string? Display { get; init; }

	/// <summary>The journeys at <paramref name="paths"/>, each called by its path relative to <paramref name="root"/> without the extension.</summary>
	public static IReadOnlyList<JourneyRunFile> FromPaths(string root, IEnumerable<string> paths) =>
		[.. paths
			.Select(p => new JourneyRunFile(
				System.IO.Path.GetRelativePath(root, p).Replace('\\', '/') is var relative && relative.EndsWith(".journey", StringComparison.Ordinal)
					? relative[..^".journey".Length]
					: relative,
				p))
			.OrderBy(f => f.Id, StringComparer.Ordinal)];
}

/// <summary>The exit codes of <see cref="JourneyConsoleRunner"/>.</summary>
public static class JourneyExitCode
{
	/// <summary>Every journey that ran passed.</summary>
	public const int Ok = 0;

	/// <summary>A transcript differs, a step failed, or every selected journey was skipped.</summary>
	public const int Failed = 1;

	/// <summary>A journey has errors in its source (see <see cref="JourneyRunOptions.ParseErrorExitCode"/>).</summary>
	public const int Usage = 2;

	/// <summary>Chrome could not start.</summary>
	public const int Browser = 3;
}

/// <summary>What <see cref="JourneyConsoleRunner"/> runs, and how.</summary>
public sealed class JourneyRunOptions
{
	/// <summary>The journeys to consider, in the order they are shown.</summary>
	public required IReadOnlyList<JourneyRunFile> Files { get; init; }

	/// <summary>What relative <c>open</c> URLs resolve against. Also the site named in the totals unless <see cref="Site"/> is set.</summary>
	public Uri? BaseUrl { get; init; }

	/// <summary>The site shown in the totals line; <see cref="BaseUrl"/> when unset.</summary>
	public Uri? Site { get; init; }

	/// <summary>Host patterns the journeys may reach on top of the base URL's host: <c>shop.test</c>, <c>*.shop.test</c>, or <c>*</c>.</summary>
	public IReadOnlyList<string> AllowedHosts { get; init; } = [];

	/// <summary>The one folder <c>file://</c> URLs may point into.</summary>
	public string? FileRoot { get; init; }

	/// <summary>Where a journey's <c>screenshot</c> and <c>network har stop</c> write; none when unset or when it returns <c>null</c>.</summary>
	public Func<JourneyRunFile, string?>? ArtifactsFor { get; init; }

	/// <summary>
	/// Compares a recorded transcript with the verified one. When <c>null</c> the transcript is not compared: the run only
	/// checks that every step, an <c>expect</c> line included, passes.
	/// </summary>
	public Func<JourneyRunFile, JourneyTranscript, SnapshotResult>? Compare { get; init; }

	/// <summary>Compares with <c>{journey}.{variant}.verified.journey</c>. Carried into the transcript for <see cref="Compare"/>.</summary>
	public string? Variant { get; init; }

	/// <summary>The enabled feature flags of the site under test.</summary>
	public IReadOnlySet<string> Features { get; init; } = new HashSet<string>(StringComparer.Ordinal);

	/// <summary>The build type of the site under test; unknown when <c>null</c>.</summary>
	public string? BuildType { get; init; }

	/// <summary>The environment of the site under test; unknown when <c>null</c>.</summary>
	public string? Env { get; init; }

	/// <summary>Refuse every request that is not GET, HEAD or OPTIONS, and skip journeys marked <c>@mutates</c> unless <see cref="AllowMutations"/>.</summary>
	public bool ReadOnly { get; init; }

	/// <summary>Run journeys marked <c>@mutates</c> on a read-only run.</summary>
	public bool AllowMutations { get; init; }

	/// <summary>URL globs a read-only run may write to anyway.</summary>
	public IReadOnlyList<string> WritableUrls { get; init; } = [];

	/// <summary>The viewport every journey starts with.</summary>
	public JourneyViewport? Viewport { get; init; }

	/// <summary>The user agent of the run.</summary>
	public string? UserAgent { get; init; }

	/// <summary>Request headers every page sends.</summary>
	public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

	/// <summary>Only journeys with one of these <c># @tags</c>; all when empty.</summary>
	public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.Ordinal);

	/// <summary>How many journeys run at once, in one shared browser. Default: 4.</summary>
	public int Parallel { get; init; } = 4;

	/// <summary>Exit with 0 when every selected journey was skipped. A run that checked nothing otherwise exits with 1.</summary>
	public bool AllowAllSkipped { get; init; }

	/// <summary>List every skipped journey instead of collapsing a reason that many share.</summary>
	public bool Verbose { get; init; }

	/// <summary>Write a JUnit XML report here.</summary>
	public string? JUnitPath { get; init; }

	/// <summary>Append a table of the outcomes to the file <c>GITHUB_STEP_SUMMARY</c> names, when that variable is set. Default: <c>true</c>.</summary>
	public bool StepSummary { get; init; } = true;

	/// <summary>The command that reruns one journey, with <c>{id}</c> standing for it. No rerun line when unset.</summary>
	public string? Rerun { get; init; }

	/// <summary>Reads a journey to judge whether it applies. <see cref="Journey.Load(string)"/> when unset; a tool with its own file system passes its own.</summary>
	public Func<string, Journey>? Load { get; init; }

	/// <summary>The exit code for a journey that cannot be parsed. Default: <see cref="JourneyExitCode.Usage"/>.</summary>
	public int ParseErrorExitCode { get; init; } = JourneyExitCode.Usage;
}

/// <summary>How one journey of a run ended.</summary>
/// <param name="File">The journey.</param>
/// <param name="Transcript">What it recorded; <c>null</c> when it was skipped or could not run.</param>
/// <param name="Compared">The comparison of its transcript with the verified one; <c>null</c> when not compared.</param>
/// <param name="Error">Why it could not run: its source has errors.</param>
/// <param name="SkipReason">Why it was skipped.</param>
/// <param name="Elapsed">How long it ran.</param>
public sealed record JourneyOutcome(
	JourneyRunFile File,
	JourneyTranscript? Transcript,
	SnapshotResult? Compared,
	string? Error,
	string? SkipReason,
	TimeSpan Elapsed
)
{
	/// <summary>The page the journey last opened, when it got that far.</summary>
	public Uri? LastOpened { get; init; }

	/// <summary>It did not run because it was not selected or does not apply.</summary>
	public bool Skipped => SkipReason is not null;

	/// <summary>Every step passed and, when compared, the transcript matches.</summary>
	public bool Passed => !Skipped && Error is null && Transcript is { Failure: null } && (Compared?.Passed ?? true);

	/// <summary>It ran or tried to, and did not pass.</summary>
	public bool Failed => !Skipped && !Passed;

	/// <summary>It failed because its source has errors.</summary>
	public bool IsParseError { get; init; }

	/// <summary>A skipped journey.</summary>
	public static JourneyOutcome Skip(JourneyRunFile file, string reason) => new(file, null, null, null, reason, TimeSpan.Zero);
}
