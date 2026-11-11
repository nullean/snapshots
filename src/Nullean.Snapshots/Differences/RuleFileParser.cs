using System.Text;

namespace Nullean.Snapshots.Differences;

internal sealed record IncludeDirective(string Path, bool Optional, RuleLocation Where);

internal sealed record ParsedRuleFile(IReadOnlyList<IncludeDirective> Includes, IReadOnlyList<DifferenceRule> Rules);

/// <summary>Reads the text of a <c>.differences</c> file.</summary>
internal static class RuleFileParser
{
	internal static readonly string[] Verbs = ["added", "removed", "changed", "ignore"];

	public static ParsedRuleFile Parse(string text, string file, RuleOrigin origin, List<string> errors)
	{
		var includes = new List<IncludeDirective>();
		var rules = new List<DifferenceRule>();
		string? section = null;
		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			var where = new RuleLocation(file, i + 1);
			var line = lines[i].Trim();
			if (line.Length == 0 || (line.StartsWith('#') && !line.StartsWith("##", StringComparison.Ordinal)))
				continue;
			if (line.StartsWith("##", StringComparison.Ordinal))
			{
				var heading = CutComment(line[2..]).Trim();
				if (heading.Length == 0)
					errors.Add($"{where}: a section needs a heading after '##'.");
				else
					section = heading;
				continue;
			}

			if (IncludeLine(line) is { } include)
			{
				// A path is not tokenized: //repo/relative would read as a regular expression.
				var path = CutComment(line[include.Length..]).Trim();
				if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
					path = path[1..^1];
				if (section is not null)
					errors.Add($"{where}: an include goes before the first '##' section.");
				else if (path.Length == 0 || (path.Contains(' ', StringComparison.Ordinal) && !line.Contains('"', StringComparison.Ordinal)))
					errors.Add($"{where}: '{include}' takes one path.");
				else
					includes.Add(new IncludeDirective(path, include == "include?", where));
				continue;
			}

			var tokens = Tokenize(line, where, errors);
			if (tokens is null || tokens.Count == 0)
				continue;
			if (ParseRule(tokens, section, where, origin, line, errors) is { } rule)
				rules.Add(rule);
		}

		return new ParsedRuleFile(includes, rules);
	}

	/// <summary><c>include</c> or <c>include?</c> when the line starts with it as a word.</summary>
	private static string? IncludeLine(string line)
	{
		foreach (var word in new[] { "include?", "include" })
		{
			if (line.StartsWith(word, StringComparison.Ordinal) && (line.Length == word.Length || char.IsWhiteSpace(line[word.Length])))
				return word;
		}

		return null;
	}

	private static DifferenceRule? ParseRule(List<RuleToken> tokens, string? section, RuleLocation where, RuleOrigin origin, string line, List<string> errors)
	{
		var at = 0;
		var maybe = tokens[0] is { Kind: RuleTokenKind.Word, Value: "maybe" };
		if (maybe)
			at++;
		if (at >= tokens.Count)
		{
			errors.Add($"{where}: 'maybe' is followed by a verb.");
			return null;
		}

		var verbToken = tokens[at];
		var verbIndex = verbToken.Kind == RuleTokenKind.Word ? Array.IndexOf(Verbs, verbToken.Value) : -1;
		if (verbIndex < 0)
		{
			errors.Add($"{where}: '{verbToken.Raw}' is not a verb: added, removed, changed or ignore (or include, include?).");
			return null;
		}

		var verb = (RuleVerb)verbIndex;
		var rest = tokens.Skip(at + 1).ToList();
		RuleValue? old = null, @new = null;
		try
		{
			if (verb == RuleVerb.Changed)
			{
				var arrow = rest.FindIndex(t => t is { Kind: RuleTokenKind.Word, Value: "->" });
				if (arrow >= 0)
				{
					if (arrow == 0 || rest.Count - arrow != 2)
					{
						errors.Add($"{where}: 'changed' is: changed [target] old -> new, each a \"quoted\" text, a /regex/ or one word.");
						return null;
					}

					old = new RuleValue(rest[arrow - 1]);
					@new = new RuleValue(rest[arrow + 1]);
					rest = [.. rest.Take(arrow - 1)];
				}
			}
		}
		catch (ArgumentException e)
		{
			errors.Add($"{where}: not a regular expression: {e.Message}");
			return null;
		}

		if (rest.Count == 0 && section is null)
		{
			errors.Add($"{where}: a rule with no target needs a '## section' above it, or it would apply to every section.");
			return null;
		}

		return new DifferenceRule(verb, maybe, section, rest, old, @new, where, origin, line);
	}

	/// <summary>Splits a rule line into words: bare, <c>"quoted"</c> or <c>/regex/</c>. A <c>#</c> after a space, then a space or the end, starts a comment.</summary>
	internal static List<RuleToken>? Tokenize(string line, RuleLocation where, List<string> errors)
	{
		var tokens = new List<RuleToken>();
		var i = 0;
		while (i < line.Length)
		{
			if (char.IsWhiteSpace(line[i]))
			{
				i++;
				continue;
			}

			if (IsComment(line, i))
				break;
			var start = i;
			var c = line[i];
			if (c is '"' or '/')
			{
				var value = new StringBuilder();
				var closed = false;
				for (i++; i < line.Length; i++)
				{
					if (line[i] == '\\' && i + 1 < line.Length)
					{
						i++;
						value.Append(line[i] switch
						{
							'n' when c == '"' => '\n',
							't' when c == '"' => '\t',
							'r' when c == '"' => '\r',
							'"' when c == '"' => '"',
							'/' when c == '/' => '/',
							'\\' when c == '"' => '\\',
							var other => AppendBoth(value, other),
						});
						continue;
					}

					if (line[i] == c)
					{
						closed = true;
						i++;
						break;
					}

					value.Append(line[i]);
				}

				if (!closed)
				{
					errors.Add($"{where}: the {(c == '"' ? "quote" : "regular expression's slash")} at column {start + 1} is not closed.");
					return null;
				}

				if (i < line.Length && !char.IsWhiteSpace(line[i]))
				{
					errors.Add($"{where}: text right after the closing {(c == '"' ? "quote" : "slash")} at column {i + 1}; put a space.");
					return null;
				}

				tokens.Add(new RuleToken(line[start..i], c == '"' ? RuleTokenKind.Quoted : RuleTokenKind.Regex, value.ToString()));
				continue;
			}

			while (i < line.Length && !char.IsWhiteSpace(line[i]))
				i++;
			var word = line[start..i];
			tokens.Add(new RuleToken(word, RuleTokenKind.Word, word));
		}

		return tokens;
	}

	// An escape that means nothing keeps its backslash, so a regex's \d stays \d.
	private static char AppendBoth(StringBuilder value, char escaped)
	{
		value.Append('\\');
		return escaped;
	}

	private static bool IsComment(string line, int i) =>
		line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])) && (i + 1 == line.Length || line[i + 1] == ' ');

	/// <summary>Cuts a trailing <c># comment</c> off a heading, which may itself hold <c>#tags</c>.</summary>
	private static string CutComment(string text)
	{
		for (var i = 0; i < text.Length; i++)
		{
			if (IsComment(text, i))
				return text[..i];
		}

		return text;
	}

	/// <summary>Quotes a value the way the parser reads it back.</summary>
	internal static string Quote(string value)
	{
		var quoted = new StringBuilder(value.Length + 2).Append('"');
		foreach (var c in value)
		{
			quoted.Append(c switch
			{
				'"' => "\\\"",
				'\\' => "\\\\",
				'\n' => "\\n",
				'\t' => "\\t",
				'\r' => "\\r",
				_ => c.ToString(),
			});
		}

		return quoted.Append('"').ToString();
	}
}
