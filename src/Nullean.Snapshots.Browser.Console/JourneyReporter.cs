using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nullean.Cdp.Journeys;

namespace Nullean.Snapshots.Browser.Console;

/// <summary>Shows a run while it happens: which journeys were skipped, which are running and where, and how each one ended.</summary>
public interface IJourneyReporter
{
	/// <summary>Called once, before anything runs, with the journeys that were skipped and how many will run.</summary>
	void Planned(IReadOnlyList<(JourneyRunFile File, string Reason)> skipped, int toRun);

	/// <summary>A journey started.</summary>
	void Started(JourneyRunFile file);

	/// <summary>A journey is about to run <paramref name="journeyStep"/>.</summary>
	void StepStarted(JourneyRunFile file, JourneyStep journeyStep);

	/// <summary>A journey ended: it passed, failed or could not run.</summary>
	void Finished(JourneyOutcome outcome);

	/// <summary>Called once, after everything ended, with every outcome in order, skipped ones included.</summary>
	void Complete(IReadOnlyList<JourneyOutcome> outcomes, TimeSpan elapsed);
}

/// <summary>One fact about the run for <see cref="ConsoleJourneyReporter.ContextBlock"/>: a label, its value, and where the value came from.</summary>
/// <param name="Label">What it is, such as <c>Env</c>.</param>
/// <param name="Value">What it is set to.</param>
/// <param name="Source">Where it came from, such as <c>--env</c>; nothing when it was detected.</param>
public sealed record ContextRow(string Label, string Value, string? Source = null);

/// <summary>
/// Writes one line per journey as it finishes. On a terminal it also keeps a status line of the running journeys at the
/// bottom, redrawn in place, and draws failures with the journey's source around the failing line; elsewhere (CI, a pipe)
/// it writes plain lines, and a failure in the shape <c>Nullean.Snapshots</c> gives it under TUnit.
/// </summary>
public sealed class ConsoleJourneyReporter : IJourneyReporter
{
	/// <summary>A reason shared by more journeys than this is shown as a count instead of a line per journey.</summary>
	internal const int CollapseAbove = 3;

	private const int Label = 11;
	private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(100);

	private readonly TextWriter _output;
	private readonly bool _interactive;
	private readonly JourneyRunOptions _options;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, string> _running = [];
	private readonly Dictionary<string, Uri> _pages = [];
	private readonly Stopwatch _sinceDraw = Stopwatch.StartNew();
	private int _finished;
	private int _total;
	private int _statusWidth;
	private bool _stale;

	/// <summary>Writes to <paramref name="output"/>; <paramref name="interactive"/> adds colour, icons and the status line.</summary>
	public ConsoleJourneyReporter(TextWriter output, JourneyRunOptions options, bool interactive)
	{
		_output = output;
		_options = options;
		_interactive = interactive;
	}

	/// <summary>True when stdout is a terminal and <c>CI</c> is not set.</summary>
	public static bool UseInteractive => !System.Console.IsOutputRedirected && Environment.GetEnvironmentVariable("CI") is not { Length: > 0 };

	private static int Width => System.Console.IsOutputRedirected || System.Console.WindowWidth <= 20 ? 100 : Math.Min(System.Console.WindowWidth, 110);

	private Uri? Site => _options.Site ?? _options.BaseUrl;

	/// <inheritdoc />
	public void Planned(IReadOnlyList<(JourneyRunFile File, string Reason)> skipped, int toRun)
	{
		lock (_gate)
		{
			_total = toRun;
			var lines = SkipLines(skipped, _options.Verbose);
			if (_interactive && lines.Count > 0)
				_output.WriteLine();
			foreach (var line in lines)
				_output.WriteLine(_interactive ? Ansi.Blue($"{Ansi.Skip} {line}") : line);
			if (_interactive && lines.Count > 0)
				_output.WriteLine();
		}
	}

	/// <inheritdoc />
	public void Started(JourneyRunFile file)
	{
		lock (_gate)
		{
			_running[file.Id] = "starting";
			DrawStatus(force: true);
		}
	}

	/// <inheritdoc />
	public void StepStarted(JourneyRunFile file, JourneyStep journeyStep)
	{
		lock (_gate)
		{
			_running[file.Id] = $"line {journeyStep.Line}";
			if (journeyStep.Arguments is ["open", var target, ..] && _options.BaseUrl is { } baseUrl && Uri.TryCreate(baseUrl, target, out var page))
				_pages[file.Id] = page;
			DrawStatus(force: false);
		}
	}

	/// <inheritdoc />
	public void Finished(JourneyOutcome outcome)
	{
		lock (_gate)
		{
			_ = _running.Remove(outcome.File.Id);
			_finished++;
			ClearStatus();
			_output.WriteLine(Colour(outcome));
			DrawStatus(force: true);
		}
	}

	/// <inheritdoc />
	public void Complete(IReadOnlyList<JourneyOutcome> outcomes, TimeSpan elapsed)
	{
		lock (_gate)
		{
			ClearStatus();
			var foldFailures = _options.GitHubActions && !_interactive && outcomes.Count(o => o.Failed) > GitHubActions.ExpandedFailures;
			foreach (var outcome in outcomes.Where(o => o.Failed))
			{
				_output.WriteLine();
				var rerun = _options.Rerun?.Replace("{id}", outcome.File.Id, StringComparison.Ordinal);
				if (!_interactive)
				{
					if (foldFailures)
						_output.WriteLine(GitHubActions.Group($"FAILED  {outcome.File.Id}"));
					_output.Write(FailureBlock(outcome, rerun));
					if (foldFailures)
						_output.WriteLine(GitHubActions.EndGroup);
					continue;
				}

				var (head, foot) = PrettyFailureParts(outcome, rerun);
				_output.Write(head);
				if (_options.ScreenshotOnFailure && outcome.Transcript?.Failure?.Screenshot is { } png && TryDrawScreenshot(png))
					_output.WriteLine();
				_output.Write(foot);
			}
			if (_options.GitHubActions && !_interactive)
				foreach (var annotation in GitHubActions.Annotations(outcomes))
					_output.WriteLine(annotation);
			_output.WriteLine();
			var passed = outcomes.Count(o => o.Passed);
			var failed = outcomes.Count(o => o.Failed);
			var skipped = outcomes.Count(o => o.Skipped);
			_output.WriteLine(_interactive ? PrettyTotals(passed, failed, skipped, elapsed) : PlainTotals(passed, failed, skipped, elapsed));
			if (passed + failed == 0)
				_output.WriteLine(
					_options.AllowAllSkipped
						? "Every selected journey was skipped; none applies to this site."
						: "Every selected journey was skipped, so nothing was checked."
				);
		}
	}

	/// <summary>A lead-in, then the facts the run starts from, one per line, and any notes below them.</summary>
	public static string ContextBlock(string lead, IReadOnlyList<ContextRow> rows, IReadOnlyList<string>? notes = null)
	{
		var text = new StringBuilder();
		_ = text.AppendLine(CultureInfo.InvariantCulture, $"{Ansi.Bold(lead)} running against:").AppendLine();
		foreach (var row in rows)
			_ = text.AppendLine(
				CultureInfo.InvariantCulture,
				$"  {row.Label.PadRight(Label)}{Ansi.Cyan(row.Value)}{(row.Source is null ? "" : $" {Ansi.Dim($"({row.Source})")}")}"
			);
		foreach (var note in notes ?? [])
			_ = text.AppendLine(CultureInfo.InvariantCulture, $"  {Ansi.Yellow("Note".PadRight(Label))}{note}");
		return text.ToString().TrimEnd();
	}

	/// <summary>The lines that say which journeys were skipped and why, collapsing a reason that many journeys share.</summary>
	internal static IReadOnlyList<string> SkipLines(IReadOnlyList<(JourneyRunFile File, string Reason)> skipped, bool listAll)
	{
		var lines = new List<string>();
		foreach (var group in skipped.GroupBy(s => s.Reason).OrderBy(g => g.Key, StringComparer.Ordinal))
		{
			var files = group.Select(g => g.File).OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
			if (listAll || files.Count <= CollapseAbove)
				lines.AddRange(files.Select(f => $"skipped {f.Id} ({group.Key})"));
			else
				lines.Add($"skipped {files.Count} journeys ({group.Key}); --verbose lists them");
		}
		return lines;
	}

	/// <summary>The plain line for one outcome.</summary>
	internal static string Line(JourneyOutcome outcome) =>
		outcome.Skipped
			? $"skipped {outcome.File.Id} ({outcome.SkipReason})"
			: outcome.Passed ? $"ok      {outcome.File.Id} ({outcome.Elapsed.TotalSeconds:0.0}s)" : $"FAILED  {outcome.File.Id}";

	private string PlainTotals(int passed, int failed, int skipped, TimeSpan elapsed) =>
		$"{passed} passed, {failed} failed, {skipped} skipped in {elapsed.TotalSeconds:0.0}s{(Site is null ? "" : $", against {Site}")}";

	private string PrettyTotals(int passed, int failed, int skipped, TimeSpan elapsed)
	{
		var counts = new List<string>
		{
			passed > 0 ? Ansi.Green($"{Ansi.Ok} {passed} passed") : $"{Ansi.Ok} {passed} passed",
			failed > 0 ? Ansi.BoldRed($"{Ansi.Fail} {failed} failed") : $"{Ansi.Fail} {failed} failed",
			skipped > 0 ? Ansi.Blue($"{Ansi.Skip} {skipped} skipped") : $"{Ansi.Skip} {skipped} skipped"
		};
		var against = Site is null ? "" : $" {Ansi.Dim("against")} {Ansi.Cyan(Site.ToString())}";
		return $"{string.Join("   ", counts)}\n\n{Ansi.Dim($"{elapsed.TotalSeconds:0.0}s")}{against}";
	}

	private static string DisplayPath(JourneyRunFile file)
	{
		if (file.Display is { } display)
			return display;
		var relative = System.IO.Path.GetRelativePath(Directory.GetCurrentDirectory(), file.Path).Replace('\\', '/');
		return relative.StartsWith("..", StringComparison.Ordinal) ? file.Path.Replace('\\', '/') : relative;
	}

	/// <summary>
	/// A failure in the shape Nullean.Snapshots gives it under TUnit: the step it stopped at, the error as written, then labelled
	/// lines. The message is shown as it came, never reflowed or cut. A transcript that differs shows the comparison's own message.
	/// </summary>
	internal string FailureBlock(JourneyOutcome outcome, string? rerun)
	{
		var where = DisplayPath(outcome.File);
		var text = new StringBuilder();
		if (outcome.Error is { } error)
			_ = text.AppendLine(CultureInfo.InvariantCulture, $"Journey did not run: {where}").AppendLine(error.TrimEnd());
		else if (outcome.Transcript?.Failure is { } failure)
		{
			_ = text.AppendLine(CultureInfo.InvariantCulture, $"Journey stopped at {failure.Describe(where)}");
			// Describe names the page the step stopped on; the last page the journey opened says something only when it could not.
			if (failure.Url is null && _pages.TryGetValue(outcome.File.Id, out var page))
				_ = text.AppendLine("Last open:".PadRight(Label) + page);
		}
		else
			_ = text.AppendLine((outcome.Compared?.FailureMessage ?? $"Journey failed: {where}").TrimEnd());
		return rerun is null ? text.ToString() : text.AppendLine("Rerun:".PadRight(Label) + rerun).ToString();
	}

	/// <summary>A failure for a person: the journey's lines around the one that failed, the error wrapped, the page and the rerun command.</summary>
	internal string PrettyFailure(JourneyOutcome outcome, string? rerun)
	{
		var (head, foot) = PrettyFailureParts(outcome, rerun);
		return head + foot;
	}

	/// <summary>The failure in two parts, so a screenshot can be drawn between the error and the page.</summary>
	private (string Head, string Foot) PrettyFailureParts(JourneyOutcome outcome, string? rerun)
	{
		var where = DisplayPath(outcome.File);
		var text = new StringBuilder();
		var failure = outcome.Transcript?.Failure;
		_ = text.AppendLine(Ansi.BoldRed($"{Ansi.Fail} {outcome.File.Id}"));
		_ = text.AppendLine(CultureInfo.InvariantCulture, $"  {Ansi.Dim($"{where}{(failure is null ? "" : $":{failure.Line}")}")}");
		if (failure is not null)
			AppendSource(text, outcome.File.Path, failure.Line);
		_ = text.AppendLine();
		if (failure is null && outcome.Error is null)
		{
			// The comparison's message is a diff with paths and commands: shown as written.
			foreach (var line in (outcome.Compared?.FailureMessage ?? "").ReplaceLineEndings("\n").TrimEnd().Split('\n'))
				_ = text.AppendLine(CultureInfo.InvariantCulture, $"  {line}");
		}
		else
			foreach (var line in Wrap(outcome.Error ?? failure!.Message, Width - 4))
				_ = text.AppendLine(CultureInfo.InvariantCulture, $"  {line}");
		_ = text.AppendLine();
		var foot = new StringBuilder();
		if (failure?.Url is { Length: > 0 } stoppedOn)
			_ = foot.AppendLine(CultureInfo.InvariantCulture, $"  {Ansi.Dim("page".PadRight(11))}{Ansi.Cyan(stoppedOn)}");
		else if (_pages.TryGetValue(outcome.File.Id, out var page))
			_ = foot.AppendLine(CultureInfo.InvariantCulture, $"  {Ansi.Dim("last open".PadRight(11))}{Ansi.Cyan(page.ToString())}");
		if (failure?.ScreenshotPath is { } screenshot)
			_ = foot.AppendLine(CultureInfo.InvariantCulture, $"  {Ansi.Dim("screenshot".PadRight(11))}{screenshot}");
		if (rerun is not null)
			_ = foot.AppendLine(CultureInfo.InvariantCulture, $"  {Ansi.Dim("rerun".PadRight(11))}{rerun}");
		return (text.ToString(), foot.ToString());
	}

	/// <summary>Draws <paramref name="png"/> with the best protocol the terminal has; false when it cannot, which never fails a run.</summary>
	private bool TryDrawScreenshot(byte[] png) => TerminalPicture.TryDraw(_output, png);

	private static void AppendSource(StringBuilder text, string path, int failedLine)
	{
		string[] lines;
		try
		{
			lines = File.ReadAllLines(path);
		}
		catch (IOException)
		{
			return;
		}
		_ = text.AppendLine();
		var last = Math.Min(lines.Length, failedLine + 1);
		for (var number = Math.Max(1, failedLine - 2); number <= last; number++)
			_ = number == failedLine
				? text.AppendLine(CultureInfo.InvariantCulture, $"  {Ansi.BoldRed($"{Ansi.Pointer} {number,3} {Ansi.Bar} {lines[number - 1]}")}")
				: text.AppendLine(CultureInfo.InvariantCulture, $"  {Ansi.Dim($"  {number,3} {Ansi.Bar} {lines[number - 1]}")}");
	}

	/// <summary>Breaks <paramref name="text"/> at spaces to fit <paramref name="width"/>; a word longer than that stays whole.</summary>
	internal static IEnumerable<string> Wrap(string text, int width)
	{
		foreach (var paragraph in text.ReplaceLineEndings("\n").TrimEnd().Split('\n'))
		{
			var line = new StringBuilder();
			foreach (var word in paragraph.Split(' '))
			{
				if (line.Length > 0 && line.Length + 1 + word.Length > width)
				{
					yield return line.ToString();
					_ = line.Clear();
				}
				_ = line.Length == 0 ? line.Append(word) : line.Append(' ').Append(word);
			}
			yield return line.ToString();
		}
	}

	private string Colour(JourneyOutcome outcome)
	{
		var line = Line(outcome);
		if (!_interactive)
			return line;
		return outcome.Passed
			? $"{Ansi.Green($"{Ansi.Ok} ok")}      {outcome.File.Id} {Ansi.Dim($"({outcome.Elapsed.TotalSeconds:0.0}s)")}"
			: $"{Ansi.BoldRed($"{Ansi.Fail} FAILED")}  {Ansi.Bold(outcome.File.Id)}";
	}

	/// <summary>Redraws the status line; a step update that comes moments after the last draw is deferred to the end of the interval, so a fast journey does not flood the terminal.</summary>
	private void DrawStatus(bool force)
	{
		if (!_interactive || _running.Count == 0)
			return;
		if (!force && _sinceDraw.Elapsed < StatusInterval)
		{
			Defer();
			return;
		}
		_stale = false;
		ClearStatus();
		var running = string.Join(" · ", _running.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key} ({r.Value})"));
		var status = $"running {_finished}/{_total} · {running}";
		var width = Width - 1;
		if (status.Length > width)
			status = status[..(width - 1)] + "…";
		_statusWidth = status.Length;
		_output.Write(Ansi.Dim(status));
		_output.Flush();
		_sinceDraw.Restart();
	}

	private void Defer()
	{
		if (_stale)
			return;
		_stale = true;
		var wait = StatusInterval - _sinceDraw.Elapsed;
		var due = wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
		_ = Task.Delay(due).ContinueWith(_ => RedrawIfStale(), TaskScheduler.Default);
	}

	private void RedrawIfStale()
	{
		lock (_gate)
		{
			if (_stale)
				DrawStatus(force: true);
		}
	}

	private void ClearStatus()
	{
		if (!_interactive || _statusWidth == 0)
			return;
		_output.Write("\r\e[K");
		_statusWidth = 0;
	}
}
