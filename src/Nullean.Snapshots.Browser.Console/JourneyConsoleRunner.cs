using System.Diagnostics;
using Nullean.Cdp;
using Nullean.Cdp.Journeys;

namespace Nullean.Snapshots.Browser.Console;

/// <summary>
/// Runs a suite of journeys in one shared Chrome, a few at a time, and shows the run as it happens. A journey that does not
/// apply to the site (<c># @requires</c>, <c># @tags</c>, <c># @mutates</c> on a read-only run) is skipped with its reason. This is
/// what <c>snap journey run</c> prints, and any other tool can call it to print the same.
/// </summary>
public static class JourneyConsoleRunner
{
	/// <summary>
	/// Runs <paramref name="options"/> in Chrome, writing the run to <paramref name="output"/> and browser start-up errors to
	/// <paramref name="error"/>. Returns a <see cref="JourneyExitCode"/>.
	/// </summary>
	public static async Task<int> RunAsync(JourneyRunOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(error);
		await using var chrome = new SharedChrome();
		return await RunAsync(
			options,
			output,
			error,
			chrome,
			new ConsoleJourneyReporter(output, options, ConsoleJourneyReporter.UseInteractive),
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <summary>How a journey is run: a real browser, or a fake in tests.</summary>
	internal interface IJourneyExecutor
	{
		/// <summary>Starts what running needs. Called once, and only when a journey will run.</summary>
		Task EnsureReadyAsync(CancellationToken cancellationToken);

		/// <summary>Runs one journey.</summary>
		Task<JourneyTranscript> RunAsync(string file, JourneySnapshotOptions options, CancellationToken cancellationToken);
	}

	internal static async Task<int> RunAsync(
		JourneyRunOptions options,
		TextWriter output,
		TextWriter error,
		IJourneyExecutor executor,
		IJourneyReporter reporter,
		CancellationToken cancellationToken
	)
	{
		_ = output;
		var clock = Stopwatch.StartNew();
		var plan = options.Files.Select(f => Plan(f, options)).ToList();
		reporter.Planned([.. plan.Where(p => p.Skip is not null).Select(p => (p.File, p.Skip!))], plan.Count(p => p.Journey is not null && p.Skip is null));

		if (plan.Any(p => p.Journey is not null && p.Skip is null))
		{
			try
			{
				await executor.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (BrowserLaunchException e)
			{
				await error.WriteLineAsync(("Error: " + e.Message).AsMemory(), CancellationToken.None).ConfigureAwait(false);
				return JourneyExitCode.Browser;
			}
		}

		using var gate = new SemaphoreSlim(Math.Max(1, options.Parallel));
		var launched = new LaunchFailure();
		var tasks = plan.Select(item => RunOneAsync(item, options, executor, reporter, gate, launched, cancellationToken));
		var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
		if (launched.Error is { } launchError)
		{
			await error.WriteLineAsync(("Error: " + launchError).AsMemory(), CancellationToken.None).ConfigureAwait(false);
			return JourneyExitCode.Browser;
		}

		reporter.Complete(outcomes, clock.Elapsed);

		if (options.JUnitPath is { } junit)
			JUnitReport.Write(junit, options.Site?.Host ?? options.BaseUrl?.Host ?? "journeys", outcomes);
		if (options.StepSummary)
			Browser
				.Console
				.StepSummary
				.Write(
					options.StepSummaryPath ?? Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"),
					options.Site ?? options.BaseUrl,
					outcomes,
					options.Rerun
				);
		return ExitCodeOf(outcomes, options);
	}

	private static int ExitCodeOf(IReadOnlyList<JourneyOutcome> outcomes, JourneyRunOptions options)
	{
		var exit = JourneyExitCode.Ok;
		if (outcomes.Any(o => o.Failed))
			exit = JourneyExitCode.Failed;
		if (outcomes.Any(o => o.IsParseError))
			exit = Math.Max(exit, options.ParseErrorExitCode);
		return exit == JourneyExitCode.Ok && !outcomes.Any(o => o.Passed) && !options.AllowAllSkipped ? JourneyExitCode.Failed : exit;
	}

	/// <summary>Chrome failing to start while a journey runs: the run ends with <see cref="JourneyExitCode.Browser"/> and no journey is shown as failed.</summary>
	private sealed class LaunchFailure
	{
		public string? Error { get; set; }
	}

	private sealed record Planned(JourneyRunFile File, Journey? Journey, string? Error, string? Skip);

	private static Planned Plan(JourneyRunFile file, JourneyRunOptions options)
	{
		Journey journey;
		try
		{
			journey = (options.Load ?? Journey.Load)(file.Path);
		}
		catch (Exception e) when (e is JourneyParseException or IOException)
		{
			return new Planned(file, null, e.Message, null);
		}

		var metadata = journey.Metadata;
		var context = new JourneyContext(options.Features, options.BuildType, options.Env);
		string? skip = null;
		if (options.Tags.Count > 0 && !metadata.Tags.Any(options.Tags.Contains))
			skip = $"no tag in [{string.Join(' ', options.Tags.Order(StringComparer.Ordinal))}]";
		else if (metadata.Unmet(context) is { } unmet)
			skip = $"requires {unmet}";
		else if (metadata.Mutates && options.ReadOnly && !options.AllowMutations)
			skip = "mutates data; a read-only run skips it";
		return new Planned(file, journey, null, skip);
	}

	private static async Task<JourneyOutcome> RunOneAsync(
		Planned item,
		JourneyRunOptions options,
		IJourneyExecutor executor,
		IJourneyReporter reporter,
		SemaphoreSlim gate,
		LaunchFailure launched,
		CancellationToken cancellationToken
	)
	{
		var file = item.File;
		if (item.Error is { } parseError)
		{
			var failed = new JourneyOutcome(file, null, null, parseError, null, TimeSpan.Zero) { IsParseError = true };
			reporter.Finished(failed);
			return failed;
		}

		if (item.Skip is { } reason)
			return JourneyOutcome.Skip(file, reason);

		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		reporter.Started(file);
		var clock = Stopwatch.StartNew();
		Uri? lastOpened = null;
		try
		{
			var run = ToRunOptions(file, options, step =>
			{
				if (step.Arguments is ["open", var target, ..] && options.BaseUrl is { } baseUrl && Uri.TryCreate(baseUrl, target, out var page))
					lastOpened = page;
				reporter.StepStarted(file, step);
			});
			var transcript = await executor.RunAsync(file.Path, run, cancellationToken).ConfigureAwait(false);
			// As in a test, a failed step still compares the partial transcript, so a received file is written for review.
			var compared = options.Compare is { } compare ? compare(file, transcript) : null;
			return Finish(new JourneyOutcome(file, transcript, compared, null, null, clock.Elapsed) { LastOpened = lastOpened });
		}
		catch (BrowserLaunchException e)
		{
			launched.Error ??= e.Message;
			return new JourneyOutcome(file, null, null, e.Message, null, clock.Elapsed);
		}
		catch (JourneyParseException e)
		{
			return Finish(new JourneyOutcome(file, null, null, e.Message, null, clock.Elapsed) { IsParseError = true });
		}
		catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			return Finish(new JourneyOutcome(file, null, null, e.Message, null, clock.Elapsed) { LastOpened = lastOpened });
		}
		finally
		{
			_ = gate.Release();
		}

		JourneyOutcome Finish(JourneyOutcome outcome)
		{
			reporter.Finished(outcome);
			return outcome;
		}
	}

	private static JourneySnapshotOptions ToRunOptions(JourneyRunFile file, JourneyRunOptions options, Action<JourneyStep> onStep) =>
		new()
		{
			Transcript = options.Compare is null ? TranscriptMode.Off : TranscriptMode.Compare,
			Variant = options.Variant,
			BaseUrl = options.BaseUrl,
			AllowedHosts = options.AllowedHosts,
			FileRoot = options.FileRoot,
			ArtifactsDirectory = options.ArtifactsFor?.Invoke(file),
			Features = options.Features,
			BuildType = options.BuildType,
			Env = options.Env,
			ReadOnly = options.ReadOnly,
			WritableUrls = options.WritableUrls,
			Viewport = options.Viewport,
			UserAgent = options.UserAgent,
			Headers = options.Headers,
			OnStep = onStep,
			ScreenshotOnFailure = options.ScreenshotOnFailure,
		};

	/// <summary>One Chrome for the whole run, started when the first journey needs it.</summary>
	private sealed class SharedChrome : IJourneyExecutor, IAsyncDisposable
	{
		private readonly Lazy<Task<Cdp.Browser>> _browser = new(() => Cdp.Browser.LaunchAsync(), LazyThreadSafetyMode.ExecutionAndPublication);

		public Task EnsureReadyAsync(CancellationToken cancellationToken) => _browser.Value;

		public async Task<JourneyTranscript> RunAsync(string file, JourneySnapshotOptions options, CancellationToken cancellationToken) =>
			await JourneySnapshot.RunAsync(file, await _browser.Value.ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

		public async ValueTask DisposeAsync()
		{
			if (_browser.IsValueCreated && _browser.Value.IsCompletedSuccessfully)
				await _browser.Value.Result.DisposeAsync().ConfigureAwait(false);
		}
	}
}
