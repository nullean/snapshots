using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Nullean.Argh;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Browser;
using Nullean.Snapshots.Browser.Console;

namespace Nullean.Snapshots.Cli;

/// <summary>
/// <c>snap journey run</c>: runs journeys and compares their transcripts with the verified snapshots, as a test would.
/// It writes received files like a test, but never accepts and never writes the run manifest: a run of some journeys
/// would make every other snapshot look orphaned.
/// </summary>
/// <param name="fileSystem">The file system, a mock in tests.</param>
/// <param name="root">The folder files and globs are relative to.</param>
/// <param name="output">Where results go.</param>
/// <param name="error">Where usage and journey errors go.</param>
/// <param name="run">Runs one journey file with its options; a real browser in the CLI, a fake in tests.</param>
/// <param name="getVariable">Reads environment variables.</param>
/// <param name="ensureReady">Starts the browser before the first journey, so a browser that cannot start ends the run once; none in tests.</param>
internal sealed class JourneyCli(
	IFileSystem fileSystem,
	string root,
	TextWriter output,
	TextWriter error,
	Func<string, JourneySnapshotOptions, CancellationToken, Task<JourneyTranscript>> run,
	Func<string, string?> getVariable,
	Func<CancellationToken, Task>? ensureReady = null
)
{
	private readonly string _root = fileSystem.Path.TrimEndingDirectorySeparator(fileSystem.Path.GetFullPath(root));

	public async Task<int> RunAsync(
		IReadOnlyList<string> patterns,
		string? baseUrl,
		CancellationToken cancellationToken,
		IReadOnlyList<string>? allow = null,
		string? fileRoot = null,
		string? artifacts = null,
		string? variant = null,
		string? transcript = null,
		JourneyRunArgs? more = null
	)
	{
		if (patterns.Count == 0)
		{
			error.WriteLine("Error: name the journeys to run: files or globs such as 'journeys/**/*.journey'.");
			return ExitCode.Usage;
		}

		if (!TryParseTranscript(transcript, out var mode, out var problem) || !TryCheckVariant(variant, out problem))
		{
			error.WriteLine(problem);
			return ExitCode.Usage;
		}

		Uri? baseUri = null;
		if (baseUrl is not null && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri) || baseUri.Scheme is not ("http" or "https" or "file")))
		{
			error.WriteLine($"Error: --base-url '{baseUrl}' is not an absolute http, https or file URL.");
			return ExitCode.Usage;
		}

		var (files, missing) = Expand(patterns);
		var lookalikes = missing.Where(LooksLikeOption).ToList();
		foreach (var pattern in missing.Except(lookalikes))
			error.WriteLine($"Error: no journey matches '{pattern}'.");
		if (lookalikes.Count > 0)
			error.WriteLine(OptionLookalikes(lookalikes));
		if (missing.Count > 0)
			return ExitCode.Usage;

		if (getVariable(SnapshotEnvironment.AcceptVariable) is { Length: > 0 })
			error.WriteLine($"{SnapshotEnvironment.AcceptVariable} is ignored: 'snap journey run' never accepts. Review with 'snap review' or accept with 'snap accept'.");

		// No test identity, no manifest flush, and the accept variable hidden: comparing is all this does. The project is
		// always the one holding the journey file, found by walking up to its csproj, never this process's assembly.
		var projects = new SnapshotProjectResolver(fileSystem);
		var engine = new SnapshotEngine(fileSystem, k => k == SnapshotEnvironment.AcceptVariable ? null : getVariable(k), () => null,
			(site, _) => WithArtifactsReceived(fileSystem, projects.FromCallerFile(site.FilePath)));
		var args = more ?? new JourneyRunArgs();
		if (!TryParseViewport(args.Viewport, out var viewport, out problem) || !TryParseHeaders(args.Headers, out var headers, out problem))
		{
			error.WriteLine(problem);
			return ExitCode.Usage;
		}

		var options = new JourneyRunOptions
		{
			Files = JourneyRunFile.FromPaths(_root, files),
			BaseUrl = baseUri,
			AllowedHosts = allow ?? [],
			FileRoot = fileRoot is null ? null : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, fileRoot)),
			// As in a test: a journey that writes files gets its own folder next to its received transcript.
			ArtifactsFor = f => artifacts is not null
				? fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, artifacts))
				: WritesFiles(f.Path) || args.ScreenshotOnFailure ? engine.ArtifactsDirectory(f.Path) : null,
			Compare = mode == TranscriptMode.Off
				? null
				: (f, recorded) => engine.MatchFile(recorded.Text, JourneySnapshot.Format, f.Path, null, null, variant),
			Variant = variant,
			Load = path => Journey.Parse(fileSystem.File.ReadAllText(path), path),
			Features = new HashSet<string>(args.Features, StringComparer.Ordinal),
			BuildType = args.BuildType,
			Env = args.Env,
			ReadOnly = args.ReadOnly,
			AllowMutations = args.AllowMutations,
			WritableUrls = args.Writable,
			Viewport = viewport,
			UserAgent = args.UserAgent,
			Headers = headers,
			Tags = new HashSet<string>(args.Tags, StringComparer.Ordinal),
			Parallel = args.Parallel,
			AllowAllSkipped = args.AllowAllSkipped,
			ScreenshotOnFailure = args.ScreenshotOnFailure,
			Verbose = args.Verbose,
			JUnitPath = args.JUnit,
			Rerun = RerunCommand(baseUrl, variant, transcript, args),
			ParseErrorExitCode = ExitCode.Usage,
		};
		var interactive = args.Interactive ?? ConsoleJourneyReporter.UseInteractive;
		if (interactive && baseUri is not null)
		{
			output.WriteLine(ConsoleJourneyReporter.ContextBlock($"snap {Version}".TrimEnd(), ContextRows(baseUri, args)));
			output.WriteLine();
		}

		return await JourneyConsoleRunner
			.RunAsync(options, output, error, new DelegateExecutor(run, ensureReady), new ConsoleJourneyReporter(output, options, interactive), cancellationToken)
			.ConfigureAwait(false);
	}

	private static string Version =>
		typeof(JourneyCli).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
			.OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

	private static List<ContextRow> ContextRows(Uri site, JourneyRunArgs args)
	{
		var rows = new List<ContextRow> { new("Site", site.ToString()) };
		if (args.Env is not null)
			rows.Add(new ContextRow("Env", args.Env, "--env"));
		if (args.BuildType is not null)
			rows.Add(new ContextRow("Build", args.BuildType, "--build-type"));
		if (args.Features.Count > 0)
			rows.Add(new ContextRow("Features", string.Join(' ', args.Features.Order(StringComparer.Ordinal)), "--feature"));
		if (args.ReadOnly)
			rows.Add(new ContextRow("Mode", "read-only", "--read-only"));
		return rows;
	}

	/// <summary>The command that reruns one journey with the same site and flags; <c>{id}</c> stands for the journey.</summary>
	internal static string RerunCommand(string? baseUrl, string? variant, string? transcript, JourneyRunArgs args)
	{
		var parts = new List<string> { "snap journey run {id}.journey" };

		void Add(string flag, string? value)
		{
			if (value is not null)
				parts.Add($"{flag} {(value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value)}");
		}

		Add("--base-url", baseUrl);
		Add("--variant", variant);
		Add("--transcript", transcript);
		Add("--env", args.Env);
		Add("--build-type", args.BuildType);
		foreach (var feature in args.Features)
			Add("--feature", feature);
		if (args.ReadOnly)
			parts.Add("--read-only");
		foreach (var writable in args.Writable)
			Add("--writable", writable);
		Add("--user-agent", args.UserAgent);
		foreach (var header in args.Headers)
			Add("--header", header);
		Add("--viewport", args.Viewport);
		if (args.AllowMutations)
			parts.Add("--allow-mutations");
		if (args.ScreenshotOnFailure)
			parts.Add("--screenshot-on-failure");
		return string.Join(' ', parts);
	}

	/// <summary>Reads <c>--viewport</c>: <c>1280x800</c>.</summary>
	internal static bool TryParseViewport(string? value, out JourneyViewport? viewport, out string problem)
	{
		problem = string.Empty;
		viewport = null;
		if (value is null)
			return true;
		var parts = value.Split('x');
		if (parts.Length == 2 && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var width)
			&& int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var height) && width > 0 && height > 0)
		{
			viewport = new JourneyViewport(width, height);
			return true;
		}

		problem = $"Error: --viewport '{value}' is not a viewport: use --viewport <width>x<height>, e.g. 1280x800.";
		return false;
	}

	/// <summary>Reads each <c>--header</c>: <c>"Name: value"</c>.</summary>
	internal static bool TryParseHeaders(IReadOnlyList<string> values, out Dictionary<string, string> headers, out string problem)
	{
		problem = string.Empty;
		headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var value in values)
		{
			var colon = value.IndexOf(':', StringComparison.Ordinal);
			if (colon <= 0)
			{
				problem = $"Error: --header '{value}' is not a header: use --header \"Name: value\".";
				return false;
			}

			headers[value[..colon].Trim()] = value[(colon + 1)..].Trim();
		}

		return true;
	}

	/// <summary>Reads <c>--transcript</c>: <c>compare</c> (the default), <c>off</c>, or <c>shape</c> where <paramref name="allowShape"/>.</summary>
	internal static bool TryParseTranscript(string? value, out TranscriptMode mode, out string problem, bool allowShape = false)
	{
		problem = string.Empty;
		mode = TranscriptMode.Compare;
		if (value is null || value.Equals("compare", StringComparison.OrdinalIgnoreCase))
			return true;
		if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			mode = TranscriptMode.Off;
			return true;
		}

		if (allowShape && value.Equals("shape", StringComparison.OrdinalIgnoreCase))
		{
			mode = TranscriptMode.Shape;
			return true;
		}

		problem = allowShape
			? $"Error: --transcript '{value}' is not 'compare', 'off' or 'shape'."
			: $"Error: --transcript '{value}' is not 'compare' or 'off'.";
		return false;
	}

	/// <summary>Checks <c>--variant</c> the way a snapshot name is checked.</summary>
	internal static bool TryCheckVariant(string? variant, out string problem)
	{
		problem = string.Empty;
		if (variant is null || SnapshotPath.IsValidName(variant))
			return true;
		problem = $"Error: --variant '{variant}' must be a name of letters, digits, '.', '-' and '_', without a leading or trailing dot.";
		return false;
	}

	private bool WritesFiles(string file) =>
		Journey.Parse(fileSystem.File.ReadAllText(file), file).Steps.Any(s => s.Command is ScreenshotCommand or HarCommand { Action: "stop" });

	/// <summary>
	/// The walk-up resolver only knows the default received folder, <c>obj/snapshots/received</c>. A project whose tests
	/// write to an artifacts path already has <c>{artifacts}/.snapshots/received/{Project}</c>; use that, so a key's
	/// received file is in one place whichever runner wrote it.
	/// </summary>
	internal static SnapshotProject WithArtifactsReceived(IFileSystem fileSystem, SnapshotProject project)
	{
		foreach (var folder in new[] { ".artifacts", "artifacts" })
		{
			var received = fileSystem.Path.Combine(project.RepositoryRoot, folder, ".snapshots", "received", project.Name);
			if (fileSystem.Directory.Exists(received))
				return project with { ReceivedDirectory = received };
		}

		return project;
	}

	/// <summary>
	/// True for a word that is usage text, not a journey: an option, or a piece of one with its brackets (<c>[--variant</c>,
	/// <c>plain]</c>). A shell passes those through as files, and they match nothing.
	/// </summary>
	internal static bool LooksLikeOption(string word) =>
		word.StartsWith('-') || word.StartsWith("[-", StringComparison.Ordinal) || (word.EndsWith(']') && !word.Contains('['));

	/// <summary>One message for every such word, with the command they spell when the brackets are left off.</summary>
	internal static string OptionLookalikes(IReadOnlyList<string> words)
	{
		var quoted = string.Join(", ", words.Select(w => $"'{w}'"));
		var typed = string.Join(' ', words.Select(w => w.Trim('[', ']')).Where(w => w.Length > 0));
		return $"Error: {quoted} look like options from usage text, not journeys. Brackets there mark an optional part and are not typed: write {typed}.";
	}

	/// <summary>Files as given, or every <c>*.journey</c> below the root a glob matches, sorted.</summary>
	private (List<string> Files, List<string> Missing) Expand(IReadOnlyList<string> patterns)
	{
		var files = new List<string>();
		var missing = new List<string>();
		List<string>? all = null;
		foreach (var pattern in patterns)
		{
			if (!SnapCli.IsGlob(pattern))
			{
				var path = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, pattern));
				if (fileSystem.File.Exists(path))
					files.Add(path);
				else
					missing.Add(pattern);
				continue;
			}

			// Not build output, and not dot folders: verified and received transcripts end in .journey too.
			all ??= [.. fileSystem.Directory.EnumerateFiles(_root, "*.journey", SearchOption.AllDirectories)
				.Where(f => !fileSystem.Path.GetRelativePath(_root, f).Split('/', '\\')
					.Any(part => part is "bin" or "obj" or "node_modules" || part.StartsWith('.')))];
			var regex = new Regex("^" + FileGlob(pattern.Replace('\\', '/').TrimStart('.', '/')) + "$", RegexOptions.CultureInvariant);
			var matched = all.Where(f => regex.IsMatch(fileSystem.Path.GetRelativePath(_root, f).Replace('\\', '/'))).ToList();
			if (matched.Count == 0)
				missing.Add(pattern);
			files.AddRange(matched);
		}

		return ([.. files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], missing);
	}

	// As for keys, but "**/" also matches no folder at all: journeys/**/*.journey includes journeys/a.journey.
	internal static string FileGlob(string pattern) =>
		string.Join("(?:.*/)?", pattern.Split("**/").Select(SnapCli.Glob));
}

/// <summary>The options of <c>snap journey run</c> beyond the files, the base URL and what to compare.</summary>
internal sealed record JourneyRunArgs
{
	public IReadOnlyList<string> Features { get; init; } = [];
	public string? Env { get; init; }
	public string? BuildType { get; init; }
	public bool ReadOnly { get; init; }
	public bool AllowMutations { get; init; }
	public IReadOnlyList<string> Writable { get; init; } = [];
	public string? UserAgent { get; init; }
	public IReadOnlyList<string> Headers { get; init; } = [];
	public string? Viewport { get; init; }
	public int Parallel { get; init; } = 4;
	public IReadOnlyList<string> Tags { get; init; } = [];
	public string? JUnit { get; init; }
	public bool Verbose { get; init; }
	public bool AllowAllSkipped { get; init; }
	public bool ScreenshotOnFailure { get; init; }

	/// <summary>Colour, icons and the status line; decided from the terminal when <c>null</c>.</summary>
	public bool? Interactive { get; init; }
}

/// <summary>Runs journeys through the delegate the CLI was given: a real browser in the tool, a fake in tests.</summary>
internal sealed class DelegateExecutor(
	Func<string, JourneySnapshotOptions, CancellationToken, Task<JourneyTranscript>> run,
	Func<CancellationToken, Task>? ensureReady
) : JourneyConsoleRunner.IJourneyExecutor
{
	public Task EnsureReadyAsync(CancellationToken cancellationToken) => ensureReady?.Invoke(cancellationToken) ?? Task.CompletedTask;

	public Task<JourneyTranscript> RunAsync(string file, JourneySnapshotOptions options, CancellationToken cancellationToken) =>
		run(file, options, cancellationToken);
}

/// <summary>Run browser journeys and compare their transcripts with the verified snapshots.</summary>
internal sealed class JourneyCommands
{
	/// <summary>
	/// Run journeys in Chrome and compare each transcript with its verified snapshot, as a test does. Writes received
	/// files for 'snap review'; never accepts. Exit codes: 0 all match, 1 a transcript differs or a step failed,
	/// 2 usage or a journey error, 3 Chrome could not start.
	/// </summary>
	/// <param name="files">Journey files or globs, relative to the root (e.g. 'tests/Shop.Tests/journeys/**/*.journey').</param>
	/// <param name="baseUrl">What relative 'open' URLs resolve against, e.g. http://localhost:5000/.</param>
	/// <param name="root">The folder files and globs are relative to. Defaults to the current folder.</param>
	/// <param name="allow">-a, --allow, Another host pattern journeys may reach (repeatable): host, *.domain or *.</param>
	/// <param name="fileRoot">The folder file:// URLs may point into, when the base URL is not a folder.</param>
	/// <param name="artifacts">Where screenshot and network har stop write. Defaults to a folder per journey next to its received transcript.</param>
	/// <param name="variant">Compare with {journey}.{variant}.verified.journey, for output that differs per flag set or build type.</param>
	/// <param name="transcript">compare (the default), or off to run the steps and expect lines without comparing the transcript.</param>
	/// <param name="feature">A feature flag that is on (repeatable), for 'feature' blocks and '# @requires' lines.</param>
	/// <param name="env">The environment's name, for 'env:' conditions.</param>
	/// <param name="buildType">The build type, for 'build-type:' conditions.</param>
	/// <param name="readOnly">Refuse every request that is not GET, HEAD or OPTIONS, and skip journeys marked '# @mutates'.</param>
	/// <param name="allowMutations">Run journeys marked '# @mutates' on a read-only run.</param>
	/// <param name="writable">URL glob a read-only run may write to anyway (repeatable): ** anything, * anything but /.</param>
	/// <param name="userAgent">The user agent of the run.</param>
	/// <param name="header">A request header every page sends (repeatable): "Name: value".</param>
	/// <param name="viewport">The viewport every journey starts with: 1280x800. Chrome's default is a phone layout on a responsive site.</param>
	/// <param name="parallel">-p, How many journeys run at once, in one shared browser. Default: 4.</param>
	/// <param name="tag">-t, Only journeys with one of these '# @tags' (repeatable).</param>
	/// <param name="junit">Write a JUnit XML report here.</param>
	/// <param name="verbose">-v, List every skipped journey instead of collapsing a reason that many share.</param>
	/// <param name="allowAllSkipped">Exit with 0 when every selected journey was skipped. Otherwise a run that checked nothing exits with 1.</param>
	/// <param name="screenshotOnFailure">Take a screenshot when a step fails: written to failure.png in the journey's artifacts folder, and drawn under the failure on a terminal that can show images. Off unless asked for, because a page can show private data.</param>
	public static async Task<int> Run(
		[Argument] string[] files,
		string? baseUrl = null,
		string? root = null,
		string[]? allow = null,
		string? fileRoot = null,
		string? artifacts = null,
		string? variant = null,
		string? transcript = null,
		string[]? feature = null,
		string? env = null,
		string? buildType = null,
		bool readOnly = false,
		bool allowMutations = false,
		string[]? writable = null,
		string? userAgent = null,
		string[]? header = null,
		string? viewport = null,
		int parallel = 4,
		string[]? tag = null,
		string? junit = null,
		bool verbose = false,
		bool allowAllSkipped = false,
		bool screenshotOnFailure = false
	)
	{
		var browser = new Lazy<Task<Cdp.Browser>>(() => Cdp.Browser.LaunchAsync(), LazyThreadSafetyMode.ExecutionAndPublication);
		try
		{
			var cli = new JourneyCli(
				new FileSystem(),
				root ?? Directory.GetCurrentDirectory(),
				Console.Out,
				Console.Error,
				async (file, options, ct) => await JourneySnapshot.RunAsync(file, await browser.Value.ConfigureAwait(false), options, ct).ConfigureAwait(false),
				Environment.GetEnvironmentVariable,
				_ => browser.Value
			);
			var args = new JourneyRunArgs
			{
				Features = feature ?? [],
				Env = env,
				BuildType = buildType,
				ReadOnly = readOnly,
				AllowMutations = allowMutations,
				Writable = writable ?? [],
				UserAgent = userAgent,
				Headers = header ?? [],
				Viewport = viewport,
				Parallel = parallel,
				Tags = tag ?? [],
				JUnit = junit,
				Verbose = verbose,
				AllowAllSkipped = allowAllSkipped,
				ScreenshotOnFailure = screenshotOnFailure,
			};
			return await cli.RunAsync(files, baseUrl, CancellationToken.None, allow, fileRoot, artifacts, variant, transcript, args).ConfigureAwait(false);
		}
		finally
		{
			if (browser.IsValueCreated && browser.Value.IsCompletedSuccessfully)
				await browser.Value.Result.DisposeAsync().ConfigureAwait(false);
		}
	}
}
