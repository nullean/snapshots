using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Nullean.Argh;
using Nullean.HttpFileParser;
using Nullean.Snapshots.Differences;
using Nullean.Snapshots.Http;

namespace Nullean.Snapshots.Cli;

/// <summary>
/// <c>snap http run</c>: runs <c>.http</c> files and compares their transcripts with the verified snapshots, as a test
/// would. It writes received files like a test, but never accepts and never writes the run manifest: a run of some
/// files would make every other snapshot look orphaned.
/// </summary>
/// <param name="fileSystem">The file system, a mock in tests.</param>
/// <param name="root">The folder files and globs are relative to.</param>
/// <param name="output">Where results go.</param>
/// <param name="error">Where usage and parse errors go.</param>
/// <param name="run">Runs one <c>.http</c> file with its options; the runner's own client in the CLI, a fake in tests.</param>
/// <param name="getVariable">Reads environment variables.</param>
internal sealed class HttpCli(
	IFileSystem fileSystem,
	string root,
	TextWriter output,
	TextWriter error,
	Func<string, HttpFileSnapshotOptions, CancellationToken, Task<HttpTranscript>> run,
	Func<string, string?> getVariable
)
{
	private readonly string _root = fileSystem.Path.TrimEndingDirectorySeparator(fileSystem.Path.GetFullPath(root));

	public async Task<int> RunAsync(
		IReadOnlyList<string> patterns,
		string? environment,
		IReadOnlyList<string>? variables,
		CancellationToken cancellationToken,
		IReadOnlyList<string>? headers = null,
		string? variant = null,
		string? transcript = null,
		bool readOnly = false
	)
	{
		if (patterns.Count == 0)
		{
			error.WriteLine("Error: name the .http files to run: files or globs such as 'http/**/*.http'.");
			return ExitCode.Usage;
		}

		if (!JourneyCli.TryParseTranscript(transcript, out var mode, out var problem, allowShape: true) || !JourneyCli.TryCheckVariant(variant, out problem))
		{
			error.WriteLine(problem);
			return ExitCode.Usage;
		}

		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var variable in variables ?? [])
		{
			var equals = variable.IndexOf('=', StringComparison.Ordinal);
			if (equals <= 0)
			{
				error.WriteLine($"Error: --var '{variable}' is not name=value.");
				return ExitCode.Usage;
			}

			values[variable[..equals].Trim()] = variable[(equals + 1)..];
		}

		var (files, missing) = Expand(patterns);
		foreach (var pattern in missing)
			error.WriteLine($"Error: no .http file matches '{pattern}'.");
		if (missing.Count > 0)
			return ExitCode.Usage;

		if (getVariable(SnapshotEnvironment.AcceptVariable) is { Length: > 0 })
			error.WriteLine($"{SnapshotEnvironment.AcceptVariable} is ignored: 'snap http run' never accepts. Review with 'snap review' or accept with 'snap accept'.");

		// No test identity, no manifest flush, and the accept variable hidden: comparing is all this does. The project is
		// always the one holding the .http file, found by walking up to its csproj, never this process's assembly.
		var projects = new SnapshotProjectResolver(fileSystem);
		var engine = new SnapshotEngine(fileSystem, k => k == SnapshotEnvironment.AcceptVariable ? null : getVariable(k), () => null,
			(site, _) => JourneyCli.WithArtifactsReceived(fileSystem, projects.FromCallerFile(site.FilePath)));
		var options = new HttpFileSnapshotOptions { Environment = environment, Variables = values, Headers = headers ?? [], Transcript = mode, Variant = variant, ReadOnly = readOnly };
		var exit = ExitCode.Ok;
		foreach (var file in files)
		{
			HttpTranscript recorded;
			try
			{
				recorded = await run(file, options, cancellationToken).ConfigureAwait(false);
			}
			catch (HttpFileParseException e)
			{
				error.WriteLine(e.Message);
				exit = Math.Max(exit, ExitCode.Usage);
				continue;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				// The file, its environment file or user secrets could not be read: not the server's doing.
				error.WriteLine($"Error: {fileSystem.Path.GetRelativePath(_root, file).Replace('\\', '/')}: {e.Message}");
				exit = Math.Max(exit, ExitCode.Io);
				continue;
			}

			var compared = mode == TranscriptMode.Off
				? new SnapshotResult(true, null, string.Empty, string.Empty, string.Empty)
				: engine.MatchFile(recorded.Text, HttpFileSnapshot.Format, file, null, null, variant);
			var result = HttpFileSnapshot.WithFailure(recorded, compared);
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

	/// <summary>
	/// <c>snap http compare</c>: runs each <c>.http</c> file against the baseline site and the target, and compares the two
	/// under the file's <c>.differences</c> rules. By default both are written as shapes, so only added, dropped or
	/// retyped properties differ; <paramref name="values"/> compares values too.
	/// </summary>
	public async Task<int> CompareAsync(
		IReadOnlyList<string> patterns,
		string? baseline,
		string? target,
		CancellationToken cancellationToken,
		string? baselineEnvironment = null,
		string? targetEnvironment = null,
		IReadOnlyList<string>? variables = null,
		IReadOnlyList<string>? headers = null,
		string hostVariable = "host",
		bool values = false,
		bool reuseBaseline = false,
		string? record = null,
		string? prune = null,
		bool readOnly = false
	)
	{
		if (patterns.Count == 0)
		{
			error.WriteLine("Error: name the .http files to compare: files or globs such as 'http/**/*.http'.");
			return ExitCode.Usage;
		}

		if (!JourneyCli.TryParseSite("--baseline", baseline, out var baselineUri, out var problem) || !JourneyCli.TryParseSite("--target", target, out var targetUri, out problem))
		{
			error.WriteLine(problem);
			return ExitCode.Usage;
		}

		var shared = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var variable in variables ?? [])
		{
			var equals = variable.IndexOf('=', StringComparison.Ordinal);
			if (equals <= 0)
			{
				error.WriteLine($"Error: --var '{variable}' is not name=value.");
				return ExitCode.Usage;
			}

			shared[variable[..equals].Trim()] = variable[(equals + 1)..];
		}

		var (files, missing) = Expand(patterns);
		foreach (var pattern in missing)
			error.WriteLine($"Error: no .http file matches '{pattern}'.");
		if (missing.Count > 0)
			return ExitCode.Usage;

		var projects = new SnapshotProjectResolver(fileSystem);
		var engine = new SnapshotEngine(fileSystem, k => k == SnapshotEnvironment.AcceptVariable ? null : getVariable(k), () => null,
			(site, _) => JourneyCli.WithArtifactsReceived(fileSystem, projects.FromCallerFile(site.FilePath)));
		var recordFile = record is null ? null : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, record));
		var pruneFile = prune is null ? null : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(_root, prune));
		var exit = ExitCode.Ok;
		foreach (var file in files)
		{
			var display = fileSystem.Path.GetRelativePath(_root, file).Replace('\\', '/');
			var store = new SiteStore(
				text => engine.SeedBaseline(text, HttpFileSnapshot.Format.Extension, file, null),
				() => engine.ReadBaseline(HttpFileSnapshot.Format.Extension, file, null),
				(text, compare) => engine.CompareSites(text, HttpFileSnapshot.Format, HttpRuleTargets.Instance, file, compare));
			var options = new HttpCompareOptions
			{
				Baseline = baselineUri,
				Target = targetUri,
				BaselineEnvironment = baselineEnvironment,
				TargetEnvironment = targetEnvironment,
				HostVariable = hostVariable,
				Values = values,
				ReuseBaseline = reuseBaseline,
				Record = recordFile,
				Prune = pruneFile,
				Run = new HttpFileSnapshotOptions { Variables = shared, Headers = headers ?? [], ReadOnly = readOnly },
			};
			SiteComparisonResult result;
			try
			{
				result = await HttpFileSnapshot.CompareCoreAsync(file, options, run, store, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception e) when (e is HttpFileParseException or ExpectedDifferencesException or InvalidOperationException)
			{
				error.WriteLine(e.Message);
				exit = Math.Max(exit, ExitCode.Usage);
				continue;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				error.WriteLine($"Error: {display}: {e.Message}");
				exit = Math.Max(exit, ExitCode.Io);
				continue;
			}

			if (result.Recorded.Count > 0)
				output.WriteLine($"Recorded {result.Recorded.Count} rule(s) for {display} in {fileSystem.Path.GetRelativePath(_root, recordFile!).Replace('\\', '/')}.");
			if (result.Passed)
				output.WriteLine($"ok   {display} ({result.Application.Explained.Count} expected difference(s))");
			else
			{
				output.WriteLine(result.FailureMessage);
				output.WriteLine();
				exit = Math.Max(exit, ExitCode.Pending);
			}
		}

		return exit;
	}

	/// <summary>Files as given, or every <c>*.http</c> below the root a glob matches, sorted.</summary>
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

			all ??= [.. fileSystem.Directory.EnumerateFiles(_root, "*.http", SearchOption.AllDirectories)
				.Where(f => !fileSystem.Path.GetRelativePath(_root, f).Split('/', '\\')
					.Any(part => part is "bin" or "obj" or "node_modules" || part.StartsWith('.')))];
			var regex = new Regex("^" + JourneyCli.FileGlob(pattern.Replace('\\', '/').TrimStart('.', '/')) + "$", RegexOptions.CultureInvariant);
			var matched = all.Where(f => regex.IsMatch(fileSystem.Path.GetRelativePath(_root, f).Replace('\\', '/'))).ToList();
			if (matched.Count == 0)
				missing.Add(pattern);
			files.AddRange(matched);
		}

		return ([.. files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], missing);
	}
}

/// <summary>Run .http files and compare their responses with the verified snapshots.</summary>
internal sealed class HttpCommands
{
	/// <summary>
	/// Run .http files and compare each transcript with its verified snapshot, as a test does. Writes received files for
	/// 'snap review'; never accepts. Exit codes: 0 all match, 1 a transcript differs or a request failed, 2 usage or a
	/// parse error, 3 a file, environment file or user secrets file could not be read.
	/// </summary>
	/// <param name="files">.http files or globs, relative to the root (e.g. 'tests/Shop.Tests/http/**/*.http').</param>
	/// <param name="env">The environment to select from http-client.env.json.</param>
	/// <param name="var">A variable as name=value; beats file variables and the environment.</param>
	/// <param name="header">Another response header to write besides content-type and location (repeatable), as the tests' Headers option does.</param>
	/// <param name="root">The folder files and globs are relative to. Defaults to the current folder.</param>
	/// <param name="variant">Compare with {file}.{variant}.verified.responses, for output that differs per environment or flag set.</param>
	/// <param name="transcript">compare (the default); off to send the requests without comparing the transcript; or shape to compare only the properties of JSON bodies, not their values.</param>
	/// <param name="readOnly">Refuse any request that is not GET, HEAD or OPTIONS unless it carries '# @readonly', for a live service.</param>
	public static async Task<int> Run(
		[Argument] string[] files,
		string? env = null,
		string[]? var = null,
		string[]? header = null,
		string? root = null,
		string? variant = null,
		string? transcript = null,
		bool readOnly = false
	)
	{
		var cli = new HttpCli(new FileSystem(), root ?? Directory.GetCurrentDirectory(), Console.Out, Console.Error,
			HttpFileSnapshot.RunFileAsync, Environment.GetEnvironmentVariable);
		return await cli.RunAsync(files, env, var, CancellationToken.None, header, variant, transcript, readOnly).ConfigureAwait(false);
	}
	/// <summary>
	/// Run .http files against a baseline site and a target site and compare the two, so only differences nobody expected
	/// fail. Expected ones are rules in {file}.differences next to the file (which may include? a file for one preview's
	/// differences). Both runs are written as shapes unless --values is given. Exit codes: 0 every difference is expected,
	/// 1 one is not (or a rule is stale, or a request failed), 2 usage, bad rules or a parse error, 3 a file could not be read.
	/// </summary>
	/// <param name="files">.http files or globs, relative to the root (e.g. 'tests/Shop.Tests/http/**/*.http').</param>
	/// <param name="baseline">The site to take the baseline from. Each file reaches it as {{host}}. Required.</param>
	/// <param name="target">The site to compare with it. Required.</param>
	/// <param name="baselineEnv">The environment of http-client.env.json for the baseline run.</param>
	/// <param name="targetEnv">The environment for the target run.</param>
	/// <param name="var">A variable as name=value for both runs; beats file variables and the environment.</param>
	/// <param name="header">Another response header to write besides content-type and location (repeatable).</param>
	/// <param name="hostVariable">The variable that holds each site's URL. Default: host.</param>
	/// <param name="values">Compare values as well as properties, instead of shapes.</param>
	/// <param name="reuseBaseline">Use the baselines of an earlier run, and send to the baseline site only for files that have none.</param>
	/// <param name="record">Append a rule for every unexpected difference to this file, which the file's rules must include? , instead of failing.</param>
	/// <param name="prune">Remove the stale rules of this file (a rules file that is an include?), instead of failing for them.</param>
	/// <param name="root">The folder files and globs are relative to. Defaults to the current folder.</param>
	/// <param name="readOnly">Refuse any request that is not GET, HEAD or OPTIONS unless it carries '# @readonly'. Use it against a live site.</param>
	public static async Task<int> Compare(
		[Argument] string[] files,
		string? baseline = null,
		string? target = null,
		string? baselineEnv = null,
		string? targetEnv = null,
		string[]? var = null,
		string[]? header = null,
		string hostVariable = "host",
		bool values = false,
		bool reuseBaseline = false,
		string? record = null,
		string? prune = null,
		string? root = null,
		bool readOnly = false
	)
	{
		var cli = new HttpCli(new FileSystem(), root ?? Directory.GetCurrentDirectory(), Console.Out, Console.Error,
			HttpFileSnapshot.RunFileAsync, Environment.GetEnvironmentVariable);
		return await cli.CompareAsync(files, baseline, target, CancellationToken.None, baselineEnv, targetEnv, var, header, hostVariable, values, reuseBaseline, record, prune, readOnly).ConfigureAwait(false);
	}
}
