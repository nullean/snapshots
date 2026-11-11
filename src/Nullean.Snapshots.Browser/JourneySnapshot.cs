using System.Reflection;
using System.Runtime.CompilerServices;
using Nullean.Cdp.Journeys;

namespace Nullean.Snapshots.Browser;

/// <summary>How <see cref="JourneySnapshot.RunAsync(string, JourneySnapshotOptions?, CancellationToken, string)"/> runs a journey.</summary>
public sealed class JourneySnapshotOptions
{
	/// <summary>What relative <c>open</c> URLs resolve against; its root is scrubbed to <c>{origin}</c>.</summary>
	public Uri? BaseUrl { get; init; }

	/// <summary>How long each action waits for its element; Nullean.Cdp's default (5 s) when unset.</summary>
	public TimeSpan? ActionTimeout { get; init; }

	/// <summary>How long each navigation may take; Nullean.Cdp's default (30 s) when unset.</summary>
	public TimeSpan? NavigationTimeout { get; init; }

	/// <summary>
	/// Host patterns the journey may reach on top of the base URL's host and its own <c>allow</c> lines: <c>shop.test</c>,
	/// <c>*.shop.test</c>, or <c>*</c>.
	/// </summary>
	public IReadOnlyList<string> AllowedHosts { get; init; } = [];

	/// <summary>
	/// The one folder <c>file://</c> URLs may point into; the base URL's folder when that is a file URL.
	/// </summary>
	public string? FileRoot { get; init; }

	/// <summary>
	/// Where <c>screenshot</c> and <c>network har stop</c> write. When unset, a test's journey writes into
	/// <see cref="Snapshot.ArtifactsDirectory"/>: <c>{received}/{folder}/{name}.artifacts</c>, emptied before each run.
	/// </summary>
	public string? ArtifactsDirectory { get; init; }

	/// <summary>Resolves <c>$env:NAME</c> references; the process environment when unset.</summary>
	public Func<string, string?>? Secrets { get; init; }

	/// <summary>
	/// <see cref="TranscriptMode.Off"/> runs the journey and fails on a step that fails, an <c>expect</c> line included,
	/// without comparing the transcript or needing a verified file. For a live site whose content drifts.
	/// </summary>
	public TranscriptMode Transcript { get; init; } = TranscriptMode.Compare;

	/// <summary>
	/// Compares with <c>{journey}.{variant}.verified.journey</c> instead of <c>{journey}.verified.journey</c>, for output
	/// that legitimately differs per flag set or build type. Letters, digits, <c>.</c>, <c>-</c> and <c>_</c>.
	/// </summary>
	public string? Variant { get; init; }

	/// <summary>
	/// The enabled feature flags of the site under test. <c>feature</c> blocks and <c>@requires</c> lines are judged
	/// against them (see <see cref="Context"/>); a block the run does not meet shows in the transcript as skipped.
	/// </summary>
	public IReadOnlySet<string> Features { get; init; } = new HashSet<string>(StringComparer.Ordinal);

	/// <summary>The build type of the site under test, such as <c>isolated</c> or <c>assembler</c>; unknown when <c>null</c>.</summary>
	public string? BuildType { get; init; }

	/// <summary>The environment of the site under test, such as <c>prod</c> or <c>edge</c>; unknown when <c>null</c>.</summary>
	public string? Env { get; init; }

	/// <summary>
	/// Refuses every request that is not GET, HEAD or OPTIONS, so the run cannot change a live site. A journey marked
	/// <c>@mutates</c> is skipped on such a run (see <see cref="JourneySnapshot.SkipReason(string, JourneySnapshotOptions, string)"/>).
	/// </summary>
	public bool ReadOnly { get; init; }

	/// <summary>URL globs a read-only run may write to anyway, such as a telemetry endpoint.</summary>
	public IReadOnlyList<string> WritableUrls { get; init; } = [];

	/// <summary>The viewport every journey of the run starts with; Chrome's default when <c>null</c>. A journey's own <c>set viewport</c> replaces it.</summary>
	public JourneyViewport? Viewport { get; init; }

	/// <summary>The user agent of the run. A journey's own <c>set useragent</c> replaces it.</summary>
	public string? UserAgent { get; init; }

	/// <summary>Request headers every page of the run sends. A journey's own <c>set headers</c> adds to them.</summary>
	public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

	/// <summary>
	/// Take a screenshot of the viewport when a step fails, into <c>JourneyFailure.Screenshot</c> of the transcript. Off unless asked
	/// for, because a page can show private data. A journey that fails also writes <c>failure.png</c> into its artifacts folder, which
	/// a test then gets, so the failure message names the file.
	/// </summary>
	public bool ScreenshotOnFailure { get; init; }

	/// <summary>Called before each step runs, for a progress display. Steps inside a <c>feature</c> block the run does not meet are not reported.</summary>
	public Action<JourneyStep>? OnStep { get; init; }

	/// <summary>What <c>feature</c> blocks and <c>@requires</c> lines are judged against.</summary>
	public JourneyContext Context => new(Features, BuildType, Env);
}

/// <summary>
/// Runs journeys (<c>*.journey</c> files of agent-browser commands) in Chrome and snapshots their transcripts. The
/// snapshot belongs to the journey file, so a test and <c>snap journey run</c> compare against the same verified file.
/// </summary>
public static partial class JourneySnapshot
{
	/// <summary>The transcript format.</summary>
	public static JourneySnapshotFormat Format { get; } = new();

	/// <summary>The format for one page's accessibility tree.</summary>
	public static AriaSnapshotFormat AriaFormat { get; } = new();

	// One Chrome for the whole test process, started on first use; every journey gets its own context.
	private static readonly Lazy<Task<Cdp.Browser>> SharedBrowser = new(LaunchSharedAsync, LazyThreadSafetyMode.ExecutionAndPublication);

	private static async Task<Cdp.Browser> LaunchSharedAsync()
	{
		var browser = await Cdp.Browser.LaunchAsync().ConfigureAwait(false);
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			try
			{
				browser.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
			}
			catch (AggregateException)
			{
				// Exiting anyway; the profile folder is temporary.
			}
		};
		return browser;
	}

	/// <summary>
	/// The journeys under <paramref name="folder"/> of the test project, as paths relative to the project with
	/// <c>/</c> separators, sorted. Use it as a test's data source.
	/// </summary>
	public static IReadOnlyList<string> Find(string folder = "journeys", [CallerFilePath] string callerFilePath = "")
	{
		var project = ProjectDirectory(callerFilePath);
		var root = Path.Combine(project, folder);
		if (!Directory.Exists(root))
			return [];
		return [.. Directory.EnumerateFiles(root, "*.journey", SearchOption.AllDirectories)
			.Select(f => Path.GetRelativePath(project, f).Replace('\\', '/'))
			.Order(StringComparer.Ordinal)];
	}

	/// <summary>
	/// Runs a journey in the shared Chrome. A relative <paramref name="journeyFile"/> is relative to the test project.
	/// A step that fails stops the run; the transcript then holds the checkpoints before it and the failure.
	/// </summary>
	public static async Task<JourneyTranscript> RunAsync(
		string journeyFile,
		JourneySnapshotOptions? options = null,
		CancellationToken cancellationToken = default,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		var file = Path.IsPathRooted(journeyFile) ? journeyFile : Path.Combine(ProjectDirectory(callerFilePath), journeyFile);
		options ??= new JourneySnapshotOptions();
		// Only journeys that write files get a folder, so the rest leave nothing behind.
		if (options.ArtifactsDirectory is null && (options.ScreenshotOnFailure || WritesFiles(file)))
			options = With(options, Snapshot.ArtifactsDirectory(file));
		var browser = await SharedBrowser.Value.ConfigureAwait(false);
		return await RunAsync(file, browser, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Runs a journey file in a new context of <paramref name="browser"/>.</summary>
	/// <exception cref="JourneyParseException">The journey has errors.</exception>
	public static async Task<JourneyTranscript> RunAsync(string journeyFile, Cdp.Browser browser, JourneySnapshotOptions? options, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(journeyFile);
		options ??= new JourneySnapshotOptions();
		if (options.Transcript == TranscriptMode.Shape)
			throw new ArgumentException("TranscriptMode.Shape applies to .http runs; a journey's transcript has no values to reduce to a shape.", nameof(options));
		var full = Path.GetFullPath(journeyFile);
		var journey = Journey.Load(full);
		var result = await JourneyRunner.RunAsync(journey, browser, new JourneyOptions
		{
			BaseUrl = options.BaseUrl,
			ActionTimeout = options.ActionTimeout,
			NavigationTimeout = options.NavigationTimeout,
			AllowedHosts = options.AllowedHosts,
			FileRoot = options.FileRoot,
			ArtifactsDirectory = options.ArtifactsDirectory,
			Secrets = options.Secrets ?? Environment.GetEnvironmentVariable,
			Features = options.Features,
			BuildType = options.BuildType,
			Env = options.Env,
			ReadOnly = options.ReadOnly,
			WritableUrls = options.WritableUrls,
			Viewport = options.Viewport,
			UserAgent = options.UserAgent,
			Headers = options.Headers,
			OnStep = options.OnStep,
			ScreenshotOnFailure = options.ScreenshotOnFailure,
		}, cancellationToken).ConfigureAwait(false);
		return new JourneyTranscript(full, JourneyTranscript.Render(result, options.BaseUrl), result.Failure)
		{
			Mode = options.Transcript,
			Variant = options.Variant,
		};
	}

	/// <summary>Whether a journey file has a step that writes a file (<c>screenshot</c>, <c>network har stop</c>).</summary>
	internal static bool WritesFiles(string journeyFile) =>
		Journey.Load(journeyFile).Steps.Any(s => s.Command is ScreenshotCommand or HarCommand { Action: "stop" });

	internal static JourneySnapshotOptions With(JourneySnapshotOptions options, string? artifactsDirectory = null, Uri? baseUrl = null) => new()
	{
		BaseUrl = baseUrl ?? options.BaseUrl,
		ActionTimeout = options.ActionTimeout,
		NavigationTimeout = options.NavigationTimeout,
		AllowedHosts = options.AllowedHosts,
		FileRoot = options.FileRoot,
		ArtifactsDirectory = artifactsDirectory ?? options.ArtifactsDirectory,
		Secrets = options.Secrets,
		Transcript = options.Transcript,
		Variant = options.Variant,
		Features = options.Features,
		BuildType = options.BuildType,
		Env = options.Env,
		ReadOnly = options.ReadOnly,
		WritableUrls = options.WritableUrls,
		Viewport = options.Viewport,
		UserAgent = options.UserAgent,
		Headers = options.Headers,
		OnStep = options.OnStep,
		ScreenshotOnFailure = options.ScreenshotOnFailure,
	};

	/// <summary>
	/// Why a journey should not run in <paramref name="context"/>: the first <c>@requires</c> condition it does not meet,
	/// as written (<c>@requires navigation-preview</c>). <c>null</c> when it applies. A journey without directives always applies.
	/// </summary>
	/// <param name="journeyFile">A journey file; a relative path is relative to the test project, as for <c>RunAsync</c>.</param>
	/// <param name="context">What the run knows about the site.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <exception cref="JourneyParseException">The journey has errors.</exception>
	public static string? Unmet(string journeyFile, JourneyContext context, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentException.ThrowIfNullOrEmpty(journeyFile);
		ArgumentNullException.ThrowIfNull(context);
		return Journey.Load(ResolveFile(journeyFile, callerFilePath)).Metadata.Unmet(context) is { } unmet ? $"@requires {unmet}" : null;
	}

	/// <summary>
	/// Why a journey should be skipped in a run with <paramref name="options"/>, or <c>null</c> when it should run: an unmet
	/// <c>@requires</c> condition, or <c>@mutates</c> on a <see cref="JourneySnapshotOptions.ReadOnly"/> run.
	/// </summary>
	public static string? SkipReason(string journeyFile, JourneySnapshotOptions options, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentException.ThrowIfNullOrEmpty(journeyFile);
		ArgumentNullException.ThrowIfNull(options);
		var metadata = Journey.Load(ResolveFile(journeyFile, callerFilePath)).Metadata;
		if (metadata.Unmet(options.Context) is { } unmet)
			return $"@requires {unmet}";
		return metadata.Mutates && options.ReadOnly ? "@mutates, and the run is read-only" : null;
	}

	/// <summary>The journey's header directives: tags, requirements, monitor, alerts, <c>@mutates</c>.</summary>
	public static JourneyMetadata Metadata(string journeyFile, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentException.ThrowIfNullOrEmpty(journeyFile);
		return Journey.Load(ResolveFile(journeyFile, callerFilePath)).Metadata;
	}

	private static string ResolveFile(string journeyFile, string callerFilePath) =>
		Path.IsPathRooted(journeyFile) ? journeyFile : Path.Combine(ProjectDirectory(callerFilePath), journeyFile);

	/// <summary>
	/// Compares the transcript with the journey's verified snapshot. A journey that stopped at a failing step fails
	/// even when its partial transcript matches, and the message names the step first.
	/// </summary>
	public static SnapshotResult Match(JourneyTranscript transcript, SnapshotOptions? options = null, string? because = null)
	{
		ArgumentNullException.ThrowIfNull(transcript);
		if (transcript.Mode == TranscriptMode.Off)
			return WithFailure(transcript, new SnapshotResult(true, null, string.Empty, string.Empty, string.Empty));
		return WithFailure(transcript, Snapshot.MatchFile(transcript.Text, Format, transcript.File, options, because, transcript.Variant));
	}

	internal static SnapshotResult WithFailure(JourneyTranscript transcript, SnapshotResult result)
	{
		if (transcript.Failure is not { } failure)
			return result;
		var stopped = $"Journey stopped at {failure.Describe(Relative(transcript.File))}";
		return result with
		{
			Passed = false,
			FailureMessage = result.FailureMessage is { } message ? stopped + "\n" + message : stopped,
		};
	}

	// Messages use paths relative to the working folder when the journey is below it, as the rest of the library's do.
	private static string Relative(string file)
	{
		var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
		return (relative.StartsWith("..", StringComparison.Ordinal) ? file : relative).Replace('\\', '/');
	}

	/// <summary>
	/// The test project's folder: from the assembly metadata Nullean.Snapshots' build targets write, else the nearest
	/// folder holding a <c>*.csproj</c> above <paramref name="callerFilePath"/>.
	/// </summary>
	internal static string ProjectDirectory(string callerFilePath)
	{
		var fromMetadata = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
			.LastOrDefault(a => a.Key == "Nullean.Snapshots.ProjectDirectory")?.Value;
		if (!string.IsNullOrWhiteSpace(fromMetadata))
			return fromMetadata;
		for (var dir = Path.GetDirectoryName(callerFilePath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
		{
			if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csproj").Any())
				return dir;
		}

		throw new InvalidOperationException(
			$"Cannot find the test project for '{callerFilePath}'. Reference Nullean.Snapshots from the test project so its build targets record it.");
	}
}
