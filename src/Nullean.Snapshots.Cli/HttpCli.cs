using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Nullean.Argh;
using Nullean.HttpFileParser;
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
		IReadOnlyList<string>? headers = null
	)
	{
		if (patterns.Count == 0)
		{
			error.WriteLine("Error: name the .http files to run: files or globs such as 'http/**/*.http'.");
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
		var options = new HttpFileSnapshotOptions { Environment = environment, Variables = values, Headers = headers ?? [] };
		var exit = ExitCode.Ok;
		foreach (var file in files)
		{
			HttpTranscript transcript;
			try
			{
				transcript = await run(file, options, cancellationToken).ConfigureAwait(false);
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

			var result = HttpFileSnapshot.WithFailure(transcript, engine.MatchFile(transcript.Text, HttpFileSnapshot.Format, file, null, null));
			if (result.Passed)
				output.WriteLine($"ok   {result.Key}");
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
	public static async Task<int> Run(
		[Argument] string[] files,
		string? env = null,
		string[]? var = null,
		string[]? header = null,
		string? root = null
	)
	{
		var cli = new HttpCli(new FileSystem(), root ?? Directory.GetCurrentDirectory(), Console.Out, Console.Error,
			HttpFileSnapshot.RunFileAsync, Environment.GetEnvironmentVariable);
		return await cli.RunAsync(files, env, var, CancellationToken.None, header).ConfigureAwait(false);
	}
}
