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
}

/// <summary>
/// Runs journeys (<c>*.journey</c> files of agent-browser commands) in Chrome and snapshots their transcripts. The
/// snapshot belongs to the journey file, so a test and <c>snap journey run</c> compare against the same verified file.
/// </summary>
public static class JourneySnapshot
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
		if (options.ArtifactsDirectory is null && WritesFiles(file))
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

	internal static JourneySnapshotOptions With(JourneySnapshotOptions options, string artifactsDirectory) => new()
	{
		BaseUrl = options.BaseUrl,
		ActionTimeout = options.ActionTimeout,
		NavigationTimeout = options.NavigationTimeout,
		AllowedHosts = options.AllowedHosts,
		FileRoot = options.FileRoot,
		ArtifactsDirectory = artifactsDirectory,
		Secrets = options.Secrets,
		Transcript = options.Transcript,
		Variant = options.Variant,
	};

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
