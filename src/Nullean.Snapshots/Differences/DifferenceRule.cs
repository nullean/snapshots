using System.Text.RegularExpressions;

namespace Nullean.Snapshots.Differences;

/// <summary>What a rule says about a difference.</summary>
public enum RuleVerb
{
	/// <summary>The target has something the baseline does not.</summary>
	Added,

	/// <summary>The baseline has something the target does not.</summary>
	Removed,

	/// <summary>A value is different.</summary>
	Changed,

	/// <summary>Any difference at the target, and everything below it.</summary>
	Ignore,
}

/// <summary>Where a rule, or an include, is written.</summary>
/// <param name="File">The full path of the rules file.</param>
/// <param name="Line">The 1-based line.</param>
public sealed record RuleLocation(string File, int Line)
{
	/// <inheritdoc />
	public override string ToString() => $"{File}:{Line}";
}

/// <summary>How a rules file came to be loaded.</summary>
public enum RuleOrigin
{
	/// <summary>The file next to the journey or <c>.http</c> file: its own, checked-in rules.</summary>
	Sidecar,

	/// <summary>An <c>include?</c>: the ephemeral rules of a preview, which may or may not exist.</summary>
	Optional,

	/// <summary>An <c>include</c>: rules shared by many files, so one file not using one says nothing about it.</summary>
	Shared,
}

/// <summary>The kinds of words in a rule line.</summary>
public enum RuleTokenKind
{
	/// <summary>A bare word.</summary>
	Word,

	/// <summary>A <c>"quoted"</c> string.</summary>
	Quoted,

	/// <summary>A <c>/regular expression/</c>.</summary>
	Regex,
}

/// <summary>One word of a rule line.</summary>
/// <param name="Raw">As written.</param>
/// <param name="Kind">What kind of word.</param>
/// <param name="Value">The word, without quotes or slashes and with escapes undone.</param>
public sealed record RuleToken(string Raw, RuleTokenKind Kind, string Value);

/// <summary>A text a rule compares a value with: a glob (<c>*</c> matches anything) or a regular expression.</summary>
public sealed class RuleValue
{
	private readonly Func<string, bool> _matches;

	/// <summary>A value from a word of a rule: a glob for a bare or quoted word, a regular expression for a <c>/regex/</c>.</summary>
	/// <exception cref="ArgumentException">The word is a regular expression that is not one.</exception>
	public RuleValue(RuleToken token)
	{
		ArgumentNullException.ThrowIfNull(token);
		Token = token;
		if (token.Kind == RuleTokenKind.Regex)
		{
			var regex = new Regex(token.Value, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
			_matches = regex.IsMatch;
		}
		else
		{
			var pattern = token.Value;
			_matches = text => Glob.IsMatch(pattern, text);
		}
	}

	/// <summary>The word the value was made from.</summary>
	public RuleToken Token { get; }

	/// <summary>Whether <paramref name="text"/> matches; <see langword="null"/> never does.</summary>
	public bool Matches(string? text) => text is not null && _matches(text);
}

/// <summary>One rule: an expected difference between a baseline and a target.</summary>
/// <param name="Verb">What it says.</param>
/// <param name="Maybe">The rule need not match anything; without it a rule that matches nothing is stale.</param>
/// <param name="Section">A glob over the heading of the section (step or request) it applies to; <see langword="null"/> for every section.</param>
/// <param name="Target">What it is about, as the words after the verb; the format reads them.</param>
/// <param name="Old">For <see cref="RuleVerb.Changed"/>, what the baseline's value must match.</param>
/// <param name="New">For <see cref="RuleVerb.Changed"/>, what the target's value must match.</param>
/// <param name="Where">Where it is written.</param>
/// <param name="Origin">How its file was loaded.</param>
/// <param name="Text">The line as written, without a trailing comment.</param>
public sealed record DifferenceRule(
	RuleVerb Verb,
	bool Maybe,
	string? Section,
	IReadOnlyList<RuleToken> Target,
	RuleValue? Old,
	RuleValue? New,
	RuleLocation Where,
	RuleOrigin Origin,
	string Text
);

internal static class Glob
{
	/// <summary>Whether <paramref name="text"/> is <paramref name="pattern"/>, where <c>*</c> stands for any text.</summary>
	public static bool IsMatch(string pattern, string text)
	{
		int p = 0, t = 0, star = -1, mark = 0;
		while (t < text.Length)
		{
			if (p < pattern.Length && pattern[p] == '*')
			{
				star = p++;
				mark = t;
			}
			else if (p < pattern.Length && pattern[p] == text[t])
			{
				p++;
				t++;
			}
			else if (star >= 0)
			{
				p = star + 1;
				t = ++mark;
			}
			else
				return false;
		}

		while (p < pattern.Length && pattern[p] == '*')
			p++;
		return p == pattern.Length;
	}
}
