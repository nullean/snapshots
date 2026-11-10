using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Nullean.Argh;
using Nullean.Cdp;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Browser;

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
internal sealed class JourneyCli(
	IFileSystem fileSystem,
	string root,
	TextWriter output,
	TextWriter error,
	Func<string, JourneySnapshotOptions, CancellationToken, Task<JourneyTranscript>> run,
	Func<string, string?> getVariable
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
		string? transcript = null
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
		foreach (var pattern in missing)
			error.WriteLine($"Error: no journey matches '{pattern}'.");
		if (missing.Count > 0)
			return ExitCode.Usage;

		if (getVariable(SnapshotEnvironment.AcceptVariable) is { Length: > 0 })
			error.WriteLine($"{SnapshotEnvironment.AcceptVariable} is ignored: 'snap journey run' never accepts. Review with 'snap review' or accept with 'snap accept'.");

		// No test identity, no manifest flush, and the accept variable hidden: comparing is all this does. The project is
		// always the one holding the journey file, found by walking up to its csproj, never this process's assembly.
		var projects = new SnapshotProjectResolver(fileSystem);
		var engine = new SnapshotEngine(fileSystem, k => k == SnapshotEnvironment.AcceptVariable ? null : getVariable(k), () => null,
			(site, _) => WithArtifactsReceived(fileSystem, projects.FromCallerFile(site.FilePath)));
		var exit = ExitCode.Ok;
		foreach (var file in files)
		{
			JourneyTranscript recorded;
			try
			{
				recorded = await run(file, new JourneySnapshotOptions
				{
					Transcript = mode,
					Variant = variant,
					BaseUrl = baseUri,
					AllowedHosts = allow ?? [],
					FileRoot = fileRoot is null ? null : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, fileRoot)),
					// As in a test: a journey that writes files gets its own folder next to its received transcript.
					ArtifactsDirectory = artifacts is not null
						? fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, artifacts))
						: WritesFiles(file) ? engine.ArtifactsDirectory(file) : null,
				}, cancellationToken).ConfigureAwait(false);
			}
			catch (JourneyParseException e)
			{
				error.WriteLine(e.Message);
				exit = Math.Max(exit, ExitCode.Usage);
				continue;
			}
			catch (BrowserLaunchException e)
			{
				error.WriteLine("Error: " + e.Message);
				return ExitCode.Io;
			}

			var compared = mode == TranscriptMode.Off
				? new SnapshotResult(true, null, string.Empty, string.Empty, string.Empty)
				: engine.MatchFile(recorded.Text, JourneySnapshot.Format, file, null, null, variant);
			var result = JourneySnapshot.WithFailure(recorded, compared);
			if (result.Passed)
				output.WriteLine(mode == TranscriptMode.Off
					? $"ok   {fileSystem.Path.GetRelativePath(_root, file).Replace('\\', '/')} (transcript off)"
					: $"ok   {result.Key}");
			else
			{
				output.WriteLine(result.FailureMessage);
				output.WriteLine();
				exit = Math.Max(exit, ExitCode.Pending);
			}
		}

		return exit;
	}

	/// <summary>Reads <c>--transcript</c>: <c>compare</c> (the default) or <c>off</c>.</summary>
	internal static bool TryParseTranscript(string? value, out TranscriptMode mode, out string problem)
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

		problem = $"Error: --transcript '{value}' is not 'compare' or 'off'.";
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
	public static async Task<int> Run(
		[Argument] string[] files,
		string? baseUrl = null,
		string? root = null,
		string[]? allow = null,
		string? fileRoot = null,
		string? artifacts = null,
		string? variant = null,
		string? transcript = null
	)
	{
		Cdp.Browser? browser = null;
		try
		{
			var cli = new JourneyCli(new FileSystem(), root ?? Directory.GetCurrentDirectory(), Console.Out, Console.Error, async (file, options, ct) =>
			{
				browser ??= await Cdp.Browser.LaunchAsync(cancellationToken: ct).ConfigureAwait(false);
				return await JourneySnapshot.RunAsync(file, browser, options, ct).ConfigureAwait(false);
			}, Environment.GetEnvironmentVariable);
			return await cli.RunAsync(files, baseUrl, CancellationToken.None, allow, fileRoot, artifacts, variant, transcript).ConfigureAwait(false);
		}
		finally
		{
			if (browser is not null)
				await browser.DisposeAsync().ConfigureAwait(false);
		}
	}
}
