using Nullean.HttpFileParser;
using System.Runtime.CompilerServices;
using Nullean.Snapshots.Differences;

namespace Nullean.Snapshots.Http;

/// <summary>How <see cref="HttpFileSnapshot.CompareAsync(string, HttpCompareOptions, CancellationToken, string)"/> runs a file against two sites.</summary>
public sealed class HttpCompareOptions
{
	/// <summary>The site to take the baseline from, such as production. Its origin is written as <c>{origin}</c>.</summary>
	public required Uri Baseline { get; init; }

	/// <summary>The site to compare with the baseline, such as a preview.</summary>
	public required Uri Target { get; init; }

	/// <summary>The environment of <c>http-client.env.json</c> for the baseline run; <see cref="Run"/>'s when unset.</summary>
	public string? BaselineEnvironment { get; init; }

	/// <summary>The environment for the target run; <see cref="Run"/>'s when unset.</summary>
	public string? TargetEnvironment { get; init; }

	/// <summary>The variable that holds each site's URL for its run. Default: <c>host</c>, as in <c>{{host}}/api/orders</c>.</summary>
	public string HostVariable { get; init; } = "host";

	/// <summary>Compare values as well as properties. By default both runs are written as <see cref="TranscriptMode.Shape"/>.</summary>
	public bool Values { get; init; }

	/// <summary>Use the baseline of an earlier run, and run the baseline site only when there is none.</summary>
	public bool ReuseBaseline { get; init; }

	/// <summary>Append rules for unexplained differences to this <c>include?</c> file, a full path, instead of failing.</summary>
	public string? Record { get; init; }

	/// <summary>Remove the stale rules of this file, a full path.</summary>
	public string? Prune { get; init; }

	/// <summary>Everything else about the runs (headers, secrets, read-only, ...); its <c>Variables</c>, <c>Environment</c>, <c>BaseUrl</c> and <c>Transcript</c> are set per site.</summary>
	public HttpFileSnapshotOptions Run { get; init; } = new();
}

/// <summary>Where a comparison keeps its baseline: the library's own engine, or the CLI's.</summary>
internal sealed record SiteStore(
	Func<string, string> Seed,
	Func<string?> Read,
	Func<string, SiteComparisonOptions, SiteComparisonResult> Compare
);

public static partial class HttpFileSnapshot
{
	/// <summary>
	/// Runs a <c>.http</c> file against the baseline site, then against the target, and compares the two under the rules in
	/// the file's <c>.differences</c>. The result is as <see cref="SiteComparison.Compare"/> returns it; a request that
	/// stops either run fails it, naming the side.
	/// </summary>
	/// <exception cref="HttpFileParseException">The file has syntax errors.</exception>
	/// <exception cref="ExpectedDifferencesException">The rules cannot be read or are not valid.</exception>
	public static Task<SiteComparisonResult> CompareAsync(
		string httpFile,
		HttpCompareOptions options,
		CancellationToken cancellationToken = default,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		ArgumentException.ThrowIfNullOrEmpty(httpFile);
		ArgumentNullException.ThrowIfNull(options);
		var file = Path.IsPathRooted(httpFile) ? httpFile : Path.Combine(ProjectDirectory(callerFilePath), httpFile);
		var store = new SiteStore(
			text => SiteComparison.SeedBaseline(text, Format, file),
			() => SiteComparison.ReadBaseline(Format, file),
			(text, compare) => SiteComparison.Compare(text, Format, HttpRuleTargets.Instance, file, compare));
		return CompareCoreAsync(file, options, RunFileAsync, store, cancellationToken);
	}

	internal static async Task<SiteComparisonResult> CompareCoreAsync(
		string file,
		HttpCompareOptions options,
		Func<string, HttpFileSnapshotOptions, CancellationToken, Task<HttpTranscript>> run,
		SiteStore store,
		CancellationToken cancellationToken
	)
	{
		var mode = options.Values ? TranscriptMode.Compare : TranscriptMode.Shape;
		if (!options.ReuseBaseline || store.Read() is null)
		{
			var baseline = await run(file, ForSite(options, options.Baseline, options.BaselineEnvironment, mode), cancellationToken).ConfigureAwait(false);
			if (baseline.Failure is { } failed)
				return Stopped("Baseline", failed, file);
			_ = store.Seed(baseline.Text);
		}

		var target = await run(file, ForSite(options, options.Target, options.TargetEnvironment, mode), cancellationToken).ConfigureAwait(false);
		var result = store.Compare(target.Text, new SiteComparisonOptions
		{
			Record = options.Record,
			Prune = options.Prune,
			Note = $"recorded against {options.Target}",
		});
		if (target.Failure is not { } stopped)
			return result;
		var message = $"Target stopped at {stopped.Describe(Relative(file))}";
		return result with { Passed = false, FailureMessage = result.FailureMessage is { } m ? message + "\n" + m : message };
	}

	private static SiteComparisonResult Stopped(string side, HttpFileFailure failure, string file) =>
		new(false, $"{side} stopped at {failure.Describe(Relative(file))}", new RuleApplication([], [], [], []), string.Empty, null, [], 0);

	private static HttpFileSnapshotOptions ForSite(HttpCompareOptions options, Uri site, string? environment, TranscriptMode mode)
	{
		var run = options.Run;
		var variables = new Dictionary<string, string>(run.Variables, StringComparer.Ordinal) { [options.HostVariable] = site.ToString().TrimEnd('/') };
		return new HttpFileSnapshotOptions
		{
			Client = run.Client,
			Environment = environment ?? run.Environment,
			Variables = variables,
			Secrets = run.Secrets,
			UserSecretsId = run.UserSecretsId,
			SecretsAssembly = run.SecretsAssembly,
			UserSecretsRoot = run.UserSecretsRoot,
			Headers = run.Headers,
			BaseUrl = site,
			TimeProvider = run.TimeProvider,
			Random = run.Random,
			Transcript = mode,
			ReadOnly = run.ReadOnly,
			Features = run.Features,
			BuildType = run.BuildType,
			Env = run.Env,
		};
	}
}
