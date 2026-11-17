using System.Globalization;
using Nullean.Cdp;
using Nullean.Cdp.Journeys;

namespace Nullean.Snapshots.Browser.Console;

/// <summary>
/// The command-line form of <see cref="JourneyRunOptions.Preconditions"/>, as the flags of <c>snap journey run</c> and any tool built on
/// <see cref="JourneyConsoleRunner"/> take them. Raw text, as typed: <see cref="JourneyWait.Parse"/> checks it.
/// </summary>
public sealed record JourneyWaitFlags
{
	/// <summary>
	/// The checks, each <c>header:Name==value</c> (a response header), <c>selector==value</c> (an element's text) or
	/// <c>selector@attribute==value</c> (an attribute). <c>=~</c> in place of <c>==</c> matches a regular expression and <c>=*</c> contains.
	/// </summary>
	public IReadOnlyList<string> Checks { get; init; } = [];

	/// <summary>The page the checks look at, resolved against the base URL. Default: <c>./</c>.</summary>
	public string? Path { get; init; }

	/// <summary>How long the wait lasts in all: <c>10s</c>, <c>500ms</c>, <c>2m</c>, or a number of seconds. Default: 10 seconds.</summary>
	public string? Timeout { get; init; }

	/// <summary>How long to wait between looks, in the same form. Default: 500 milliseconds.</summary>
	public string? Interval { get; init; }

	/// <summary>How many looks that fail are tried again. Default: <see cref="JourneyPrecondition.Retries"/>.</summary>
	public int? Retries { get; init; }
}

/// <summary>What <see cref="JourneyWait.Parse"/> made of the flags: the preconditions, or what is wrong with them.</summary>
/// <param name="Preconditions">One per check; empty when there is a <paramref name="Problem"/> or when no check was given.</param>
/// <param name="Problem">Plain text naming the flag at fault, without a prefix; <c>null</c> when the flags are valid.</param>
public sealed record JourneyWaitParse(IReadOnlyList<JourneyPrecondition> Preconditions, string? Problem = null)
{
	/// <summary>The flags are valid.</summary>
	public bool Valid => Problem is null;
}

/// <summary>Reads the wait flags of a command line into <see cref="JourneyPrecondition"/>s.</summary>
public static class JourneyWait
{
	private static readonly (string Token, TextMatch Match)[] Operators = [("==", TextMatch.Exact), ("=~", TextMatch.Regex), ("=*", TextMatch.Contains)];

	/// <summary>
	/// Reads <paramref name="flags"/>. The operators <c>==</c>, <c>=~</c> and <c>=*</c> are found outside quotes and brackets, and the
	/// last <c>@</c> outside them starts the attribute, so a selector such as <c>meta[name='docs:build']</c> or
	/// <c>a[href='mailto:docs@elastic.co']</c> needs no escaping. Timeout, interval and retries apply to every check.
	/// </summary>
	public static JourneyWaitParse Parse(JourneyWaitFlags flags)
	{
		ArgumentNullException.ThrowIfNull(flags);
		if (flags.Checks.Count == 0)
			return flags.Path is not null || flags.Timeout is not null || flags.Interval is not null || flags.Retries is not null
				? new JourneyWaitParse([], "--wait-path, --wait-timeout, --wait-interval and --wait-retries describe a --wait-for; give one.")
				: new JourneyWaitParse([]);

		if (
			!TryDuration("--wait-timeout", flags.Timeout, TimeSpan.FromSeconds(10), out var timeout, out var problem) ||
			!TryDuration("--wait-interval", flags.Interval, TimeSpan.FromMilliseconds(500), out var interval, out problem)
		)
			return new JourneyWaitParse([], problem);
		if (flags.Retries is < 0)
			return new JourneyWaitParse([], $"--wait-retries {flags.Retries} is not zero or more.");

		var preconditions = new List<JourneyPrecondition>();
		foreach (var check in flags.Checks)
		{
			if (!TryCheck(check, out var parsed, out problem))
				return new JourneyWaitParse([], problem);
			preconditions.Add(parsed with { Path = flags.Path ?? "./", Timeout = timeout, Interval = interval, Retries = flags.Retries ?? parsed.Retries });
		}

		return new JourneyWaitParse(preconditions);
	}

	private static bool TryCheck(string value, out JourneyPrecondition precondition, out string problem)
	{
		problem = string.Empty;
		precondition = null!;
		var (index, length, match) = FindOperator(value);
		var target = index < 0 ? string.Empty : value[..index];
		var expected = index < 0 ? string.Empty : value[(index + length)..];
		if (index < 0 || target.Length == 0 || expected.Length == 0)
		{
			problem =
				$"--wait-for '{value}' is not a check: use header:Name==value, selector==value or selector@attribute==value ('=~' matches a regular expression, '=*' contains).";
			return false;
		}

		if (target.StartsWith("header:", StringComparison.OrdinalIgnoreCase))
		{
			var name = target["header:".Length..];
			if (name.Length == 0)
			{
				problem = $"--wait-for '{value}' names no header.";
				return false;
			}

			precondition = new JourneyPrecondition { Path = "./", Header = name, Expected = expected, Match = match };
			return true;
		}

		string? attribute = null;
		if (LastTopLevel(target, '@') is var at and >= 0)
		{
			attribute = target[(at + 1)..];
			target = target[..at];
		}

		if (target.Length == 0 || attribute is { Length: 0 })
		{
			problem = $"--wait-for '{value}' is not a check: the selector or the attribute is empty.";
			return false;
		}

		precondition = new JourneyPrecondition { Path = "./", Selector = target, Attribute = attribute, Expected = expected, Match = match };
		return true;
	}

	// The first operator outside quotes and brackets, which is where the target ends and the expected value starts.
	private static (int Index, int Length, TextMatch Match) FindOperator(string value)
	{
		var scan = new Scan();
		for (var i = 0; i < value.Length; i++)
		{
			if (scan.Outside(value[i]))
			{
				foreach (var (token, match) in Operators)
					if (string.CompareOrdinal(value, i, token, 0, token.Length) == 0)
						return (i, token.Length, match);
			}
		}

		return (-1, 0, TextMatch.Exact);
	}

	private static int LastTopLevel(string text, char wanted)
	{
		var scan = new Scan();
		var last = -1;
		for (var i = 0; i < text.Length; i++)
		{
			if (scan.Outside(text[i]) && text[i] == wanted)
				last = i;
		}

		return last;
	}

	// Tracks whether the scan is inside a quoted string or a [bracket], as in a CSS attribute selector.
	private struct Scan
	{
		private char _quote;
		private int _depth;

		// Whether this character sits outside quotes and brackets; updates the state for the next one.
		public bool Outside(char c)
		{
			if (_quote != 0)
			{
				if (c == _quote)
					_quote = '\0';
				return false;
			}

			switch (c)
			{
				case '\'' or '"':
					_quote = c;
					return false;
				case '[':
					_depth++;
					return false;
				case ']' when _depth > 0:
					_depth--;
					return false;
				default:
					return _depth == 0;
			}
		}
	}

	private static bool TryDuration(string flag, string? value, TimeSpan fallback, out TimeSpan duration, out string problem)
	{
		problem = string.Empty;
		duration = fallback;
		if (value is null)
			return true;
		var (digits, unit) = value.EndsWith("ms", StringComparison.Ordinal)
			? (value[..^2], 0.001)
			: value.EndsWith('s') ? (value[..^1], 1.0) : value.EndsWith('m') ? (value[..^1], 60.0) : (value, 1.0);
		if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0)
		{
			duration = TimeSpan.FromSeconds(number * unit);
			return true;
		}

		problem = $"{flag} '{value}' is not a duration: use 10s, 500ms or 2m.";
		return false;
	}
}
