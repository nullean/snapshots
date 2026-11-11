using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Nullean.Argh;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Browser;
using Nullean.Snapshots.Browser.Console;
using Nullean.Snapshots.Differences;

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
			error.WriteLine(
				$"{SnapshotEnvironment.AcceptVariable} is ignored: 'snap journey run' never accepts. Review with 'snap review' or accept with 'snap accept'."
			);

		// No test identity, no manifest flush, and the accept variable hidden: comparing is all this does. The project is
		// always the one holding the journey file, found by walking up to its csproj, never this process's assembly.
		var projects = new SnapshotProjectResolver(fileSystem);
		var engine = new SnapshotEngine(
			fileSystem,
			k => k == SnapshotEnvironment.AcceptVariable ? null : getVariable(k),
			() => null,
			(site, _) => WithArtifactsReceived(fileSystem, projects.FromCallerFile(site.FilePath))
		);
		var args = more ?? new JourneyRunArgs();
		if (!TryParseViewport(args.Viewport, out var viewport, out problem) || !TryParseHeaders(args.Headers, out var headers, out problem))
		{
			error.WriteLine(problem);
			return ExitCode.Usage;
		}

		var options = BuildOptions(
			JourneyRunFile.FromPaths(_root, files),
			baseUri,
			allow,
			fileRoot,
			artifacts,
			variant,
			args,
			engine,
			viewport,
			headers,
			mode == TranscriptMode.Off ? null : (f, recorded) => engine.MatchFile(recorded.Text, JourneySnapshot.Format, f.Path, null, null, variant),
			RerunCommand(baseUrl, variant, transcript, args)
		);
		var interactive = args.Interactive ?? ConsoleJourneyReporter.UseInteractive;
		if (interactive && baseUri is not null)
		{
			output.WriteLine(ConsoleJourneyReporter.ContextBlock($"snap {Version}".TrimEnd(), ContextRows(baseUri, args)));
			output.WriteLine();
		}

		return await JourneyConsoleRunner.RunAsync(
			options,
			output,
			error,
			new DelegateExecutor(run, ensureReady),
			new ConsoleJourneyReporter(output, options, interactive),
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <summary>
	/// <c>snap journey compare</c>: runs each journey against the baseline site, seeding its transcript into the received
	/// tree, then against the target, and compares the two under the journey's <c>.differences</c> rules. The runs are
	/// the console runner's, so <c>@requires</c>, <c>@mutates</c>, read-only runs and parallelism are as in <c>run</c>.
	/// </summary>
	public async Task<int> CompareAsync(
		IReadOnlyList<string> patterns,
		string? baseline,
		string? target,
		CancellationToken cancellationToken,
		IReadOnlyList<string>? allow = null,
		string? fileRoot = null,
		string? artifacts = null,
		bool reuseBaseline = false,
		string? record = null,
		string? prune = null,
		JourneyRunArgs? more = null,
		string imageDiff = "report"
	)
	{
		if (patterns.Count == 0)
		{
			error.WriteLine("Error: name the journeys to compare: files or globs such as 'journeys/**/*.journey'.");
			return ExitCode.Usage;
		}

		if (imageDiff is not ("report" or "fail" or "off"))
		{
			error.WriteLine($"Error: --image-diff '{imageDiff}' is not 'report', 'fail' or 'off'.");
			return ExitCode.Usage;
		}

		if (!TryParseSite("--baseline", baseline, out var baselineUri, out var problem) || !TryParseSite("--target", target, out var targetUri, out problem))
		{
			error.WriteLine(problem);
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

		var args = more ?? new JourneyRunArgs();
		if (!TryParseViewport(args.Viewport, out var viewport, out problem) || !TryParseHeaders(args.Headers, out var headers, out problem))
		{
			error.WriteLine(problem);
			return ExitCode.Usage;
		}

		var projects = new SnapshotProjectResolver(fileSystem);
		var engine = new SnapshotEngine(
			fileSystem,
			k => k == SnapshotEnvironment.AcceptVariable ? null : getVariable(k),
			() => null,
			(site, _) => WithArtifactsReceived(fileSystem, projects.FromCallerFile(site.FilePath))
		);
		var runFiles = JourneyRunFile.FromPaths(_root, files);
		var recordFile = record is null ? null : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, record));
		var pruneFile = prune is null ? null : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, prune));
		var executor = new DelegateExecutor(run, ensureReady);
		var interactive = args.Interactive ?? ConsoleJourneyReporter.UseInteractive;

		// Pass one: the baseline site, written into the received tree. Nothing is shown unless it did not finish.
		var toSeed = reuseBaseline ? runFiles.Where(f => engine.ReadBaseline(JourneySnapshot.Format.Extension, f.Path, null) is null).ToList() : [.. runFiles];
		if (toSeed.Count > 0)
		{
			var captured = new StringWriter();
			var seedOptions = BuildOptions(
				toSeed,
				baselineUri,
				allow,
				fileRoot,
				artifacts,
				null,
				args,
				engine,
				viewport,
				headers,
				(f, recorded) =>
				{
					// A baseline that stopped at a failing step is not one to compare with.
					if (recorded.Failure is null)
						_ = engine.SeedBaseline(recorded.Text, JourneySnapshot.Format.Extension, f.Path, null);
					return new SnapshotResult(true, null, string.Empty, string.Empty, string.Empty);
				},
				string.Empty,
				seeding: true,
				role: "baseline"
			);
			var seeded = await JourneyConsoleRunner.RunAsync(
				seedOptions,
				captured,
				error,
				executor,
				new ConsoleJourneyReporter(captured, seedOptions, false),
				cancellationToken
			).ConfigureAwait(false);
			if (seeded != JourneyExitCode.Ok)
			{
				output.WriteLine($"The run against the baseline {baselineUri} did not finish, so there is nothing to compare with:");
				output.Write(captured.ToString());
				return seeded;
			}
		}

		// Pass two: the target, compared with what pass one wrote.
		var usageErrors = 0;
		var screenshots = new List<(JourneyRunFile File, IReadOnlyList<ImageDifference> Pictures)>();

		SnapshotResult CompareOne(JourneyRunFile f, JourneyTranscript recorded)
		{
			try
			{
				var result = engine.CompareSites(
					recorded.Text,
					JourneySnapshot.Format,
					JourneyRuleTargets.Instance,
					f.Path,
					new SiteComparisonOptions { Record = recordFile, Prune = pruneFile, Note = $"recorded against {targetUri}" }
				);
				if (result.Recorded.Count > 0)
					output.WriteLine(
						$"Recorded {result.Recorded.Count} rule(s) for {f.Id} in {fileSystem.Path.GetRelativePath(_root, recordFile!).Replace('\\', '/')}."
					);
				var passed = result.Passed;
				var message = result.FailureMessage;
				if (
					imageDiff != "off" && artifacts is null &&
					SiteImages.Compare(
						fileSystem,
						engine.SiteArtifactsDirectory(f.Path, "baseline", clean: false),
						engine.SiteArtifactsDirectory(f.Path, "target", clean: false)
					) is { Count: > 0 } pictures
				)
				{
					lock (screenshots)
						screenshots.Add((f, pictures));
					if (imageDiff == "fail")
					{
						passed = false;
						message = (message is null ? string.Empty : message + "\n") + $"{pictures.Count} screenshot(s) differ: " + string.Join(
							", ",
							pictures.Select(p => p.Name)
						);
					}
				}

				return new SnapshotResult(passed, message, string.Empty, result.BaselinePath, result.TargetPath ?? string.Empty);
			}
			catch (Exception e) when (e is ExpectedDifferencesException or InvalidOperationException)
			{
				_ = Interlocked.Increment(ref usageErrors);
				return new SnapshotResult(false, e.Message, string.Empty, string.Empty, string.Empty);
			}
		}

		var options = BuildOptions(
			runFiles,
			targetUri,
			allow,
			fileRoot,
			artifacts,
			null,
			args,
			engine,
			viewport,
			headers,
			CompareOne,
			$"snap journey compare {{id}}.journey --baseline {baselineUri} --target {targetUri} --reuse-baseline",
			role: "target"
		);
		if (interactive)
		{
			output.WriteLine(ConsoleJourneyReporter.ContextBlock(
				$"snap {Version}".TrimEnd(),
				[new ContextRow("Baseline", baselineUri.ToString()), new ContextRow("Target", targetUri.ToString())]
			));
			output.WriteLine();
		}

		var exit = await JourneyConsoleRunner.RunAsync(
			options,
			output,
			error,
			executor,
			new ConsoleJourneyReporter(output, options, interactive),
			cancellationToken
		).ConfigureAwait(false);
		if (screenshots.Count > 0)
			ReportScreenshots(screenshots, imageDiff == "fail", interactive);
		return usageErrors > 0 ? Math.Max(exit, ExitCode.Usage) : exit;
	}

	/// <summary>The screenshots that differ between the two sites, each with its diff image, drawn when the terminal can show one.</summary>
	private void ReportScreenshots(List<(JourneyRunFile File, IReadOnlyList<ImageDifference> Pictures)> screenshots, bool failing, bool draw)
	{
		var count = screenshots.Sum(s => s.Pictures.Count);
		output.WriteLine();
		output.WriteLine(
			$"{count} screenshot(s) differ between the baseline and the target{(failing ? string.Empty : " (not a failure: --image-diff fail makes it one)")}:"
		);
		foreach (var (file, pictures) in screenshots.OrderBy(s => s.File.Id, StringComparer.Ordinal))
		{
			foreach (var picture in pictures)
			{
				output.WriteLine($"  {file.Id}  {picture.Name}: {picture.Summary}");
				foreach (var region in picture.Regions)
					output.WriteLine($"      {region}");
				if (picture.DiffPath is not { } diff)
					continue;
				output.WriteLine($"      diff: {fileSystem.Path.GetRelativePath(_root, diff).Replace('\\', '/')}");
				if (draw && fileSystem.File.Exists(diff) && TerminalPicture.TryDraw(output, fileSystem.File.ReadAllBytes(diff)))
					output.WriteLine();
			}
		}
	}

	/// <summary>Reads a required <c>--baseline</c> or <c>--target</c>: an absolute http, https or file URL.</summary>
	internal static bool TryParseSite(string flag, string? value, out Uri uri, out string problem)
	{
		problem = string.Empty;
		uri = null!;
		if (value is null)
		{
			problem = $"Error: {flag} is required: the URL of the site to {(flag == "--baseline" ? "take the baseline from" : "compare with it")}.";
			return false;
		}

		if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "file")
		{
			uri = parsed;
			return true;
		}

		problem = $"Error: {flag} '{value}' is not an absolute http, https or file URL.";
		return false;
	}

	private JourneyRunOptions BuildOptions(
		IReadOnlyList<JourneyRunFile> files,
		Uri? baseUri,
		IReadOnlyList<string>? allow,
		string? fileRoot,
		string? artifacts,
		string? variant,
		JourneyRunArgs args,
		SnapshotEngine engine,
		JourneyViewport? viewport,
		IReadOnlyDictionary<string, string> headers,
		Func<JourneyRunFile, JourneyTranscript, SnapshotResult>? compare,
		string rerun,
		bool seeding = false,
		string? role = null
	) =>
		new()
		{
			Files = files,
			BaseUrl = baseUri,
			AllowedHosts = allow ?? [],
			FileRoot = fileRoot is null ? null : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, fileRoot)),
			// As in a test: a journey that writes files gets its own folder next to its received transcript.
			// A comparison keeps a folder per site, so the second run does not empty the first's screenshots.
			ArtifactsFor =
				f => artifacts is not null
					? fileSystem.Path.GetFullPath(role is null ? fileSystem.Path.Combine(_root, artifacts) : fileSystem.Path.Combine(_root, artifacts, role))
					: WritesFiles(f.Path) || args.ScreenshotOnFailure
						? (role is null ? engine.ArtifactsDirectory(f.Path) : engine.SiteArtifactsDirectory(f.Path, role, clean: true))
						: null,
			Compare = compare,
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
			// The baseline pass is not the run: a journey it skips is skipped in the comparison too, and it reports to no one.
			AllowAllSkipped = seeding || args.AllowAllSkipped,
			ScreenshotOnFailure = args.ScreenshotOnFailure,
			Verbose = args.Verbose,
			JUnitPath = seeding ? null : args.JUnit,
			StepSummary = !seeding,
			Rerun = rerun,
			ParseErrorExitCode = ExitCode.Usage,
		};

	private static string Version =>
		typeof(JourneyCli)
			.Assembly
			.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
			.OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
			.FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

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
		if (
			parts.Length == 2 && int.TryParse(
				parts[0],
				System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture,
				out var width
			) && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var height) && width >
				0 && height > 0
		)
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
		headers = [with(StringComparer.OrdinalIgnoreCase)];
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
			all ??=
			[
				.. fileSystem
					.Directory
					.EnumerateFiles(_root, "*.journey", SearchOption.AllDirectories)
					.Where(
						f =>
							!fileSystem
								.Path
								.GetRelativePath(_root, f)
								.Split('/', '\\')
								.Any(part => part is "bin" or "obj" or "node_modules" || part.StartsWith('.'))
					)
			];
			var regex = new Regex("^" + FileGlob(pattern.Replace('\\', '/').TrimStart('.', '/')) + "$", RegexOptions.CultureInvariant);
			var matched = all.Where(f => regex.IsMatch(fileSystem.Path.GetRelativePath(_root, f).Replace('\\', '/'))).ToList();
			if (matched.Count == 0)
				missing.Add(pattern);
			files.AddRange(matched);
		}

		return ([.. files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], missing);
	}

	// As for keys, but "**/" also matches no folder at all: journeys/**/*.journey includes journeys/a.journey.
	internal static string FileGlob(string pattern) => string.Join("(?:.*/)?", pattern.Split("**/").Select(SnapCli.Glob));
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
	/// <summary>
	/// Run journeys against a baseline site and a target site and compare the two transcripts, so only differences nobody
	/// expected fail. Expected ones are rules in {journey}.differences next to the journey (which may include? a file for one
	/// preview's differences). The baseline is seeded into the received tree, never a snapshot. Exit codes: 0 every difference
	/// is expected, 1 a difference is not (or a rule is stale, or a step failed), 2 usage, bad rules or a journey error,
	/// 3 Chrome could not start.
	/// </summary>
	/// <param name="files">Journey files or globs, relative to the root (e.g. 'tests/Shop.Tests/journeys/**/*.journey').</param>
	/// <param name="baseline">The site to take the baseline from, such as https://docs.example.com/. Required.</param>
	/// <param name="target">The site to compare with it, such as https://docs-preview.example.com/. Required.</param>
	/// <param name="reuseBaseline">Use the baselines of an earlier run, and run the baseline site only for journeys that have none.</param>
	/// <param name="record">Append a rule for every unexpected difference to this file, which the journey's rules must include? , instead of failing.</param>
	/// <param name="prune">Remove the stale rules of this file (a rules file that is an include?), instead of failing for them.</param>
	/// <param name="root">The folder files and globs are relative to. Defaults to the current folder.</param>
	/// <param name="allow">-a, --allow, Another host pattern journeys may reach (repeatable): host, *.domain or *.</param>
	/// <param name="fileRoot">The folder file:// URLs may point into, when a site is not a folder.</param>
	/// <param name="artifacts">Where screenshot and network har stop write. Defaults to a folder per journey next to its received transcript.</param>
	/// <param name="feature">A feature flag that is on (repeatable), for 'feature' blocks and '# @requires' lines.</param>
	/// <param name="env">The environment's name, for 'env:' conditions.</param>
	/// <param name="buildType">The build type, for 'build-type:' conditions.</param>
	/// <param name="readOnly">Refuse every request that is not GET, HEAD or OPTIONS, and skip journeys marked '# @mutates'.</param>
	/// <param name="allowMutations">Run journeys marked '# @mutates' on a read-only run.</param>
	/// <param name="writable">URL glob a read-only run may write to anyway (repeatable): ** anything, * anything but /.</param>
	/// <param name="userAgent">The user agent of the run.</param>
	/// <param name="header">A request header every page sends (repeatable): "Name: value".</param>
	/// <param name="viewport">The viewport every journey starts with: 1280x800.</param>
	/// <param name="parallel">-p, How many journeys run at once, in one shared browser. Default: 4.</param>
	/// <param name="tag">-t, Only journeys with one of these '# @tags' (repeatable).</param>
	/// <param name="junit">Write a JUnit XML report of the comparison here.</param>
	/// <param name="verbose">-v, List every skipped journey instead of collapsing a reason that many share.</param>
	/// <param name="allowAllSkipped">Exit with 0 when every selected journey was skipped. Otherwise a run that checked nothing exits with 1.</param>
	/// <param name="imageDiff">What a screenshot that differs between the sites means: report (the default: list it with its diff image), fail, or off. Screenshots come from the journey's 'screenshot' steps, one folder per site.</param>
	/// <param name="screenshotOnFailure">Take a screenshot when a step fails, on either site: written to failure.png in that site's artifacts folder, and drawn under the failure on a terminal that can show images. Off unless asked for, because a page can show private data.</param>
	public static async Task<int> Compare(
		[Argument] string[] files,
		string? baseline = null,
		string? target = null,
		bool reuseBaseline = false,
		string? record = null,
		string? prune = null,
		string? root = null,
		string[]? allow = null,
		string? fileRoot = null,
		string? artifacts = null,
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
		string imageDiff = "report",
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
			return await cli.CompareAsync(
				files,
				baseline,
				target,
				CancellationToken.None,
				allow,
				fileRoot,
				artifacts,
				reuseBaseline,
				record,
				prune,
				args,
				imageDiff
			).ConfigureAwait(false);
		}
		finally
		{
			if (browser.IsValueCreated && browser.Value.IsCompletedSuccessfully)
				await browser.Value.Result.DisposeAsync().ConfigureAwait(false);
		}
	}
}
