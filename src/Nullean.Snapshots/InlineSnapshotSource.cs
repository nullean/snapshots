using System.Globalization;
using System.Text;

namespace Nullean.Snapshots;

/// <summary>An inline snapshot cannot be located or rewritten in its source file.</summary>
internal sealed class InlineSnapshotException(string message) : Exception(message);

/// <summary>
/// One literal to rewrite: the assertion call (its method name and the line the compiler reported), the source text of
/// its <c>expected</c> argument as the compiler saw it, the value that literal held when the test ran, and the value
/// to write.
/// </summary>
/// <param name="MethodName">The assertion method, e.g. <c>MatchInlineSnapshot</c>.</param>
/// <param name="Line">The 1-based line <c>[CallerLineNumber]</c> reported.</param>
/// <param name="Expression">
/// The source text of the <c>expected</c> argument (<c>[CallerArgumentExpression]</c>), or null when it was left out.
/// </param>
/// <param name="Expected">The literal's value when the test ran, or null when there was none.</param>
/// <param name="NewValue">The value to write, in the on-disk form (<see cref="SnapshotText.Normalize"/>).</param>
internal sealed record InlineEdit(string MethodName, int Line, string? Expression, string? Expected, string NewValue);

/// <summary>The rewritten source and, per edit, why it was not applied (null when it was).</summary>
internal sealed record InlineRewrite(string Source, IReadOnlyList<string?> Errors);

/// <summary>
/// Finds, reads and rewrites the string literal of an inline snapshot in C# source. It lexes just enough C# to skip
/// comments, characters and every kind of string, rather than referencing Roslyn: the CLI is a native AOT tool and this
/// stays small. See DECISIONS.md, "Inline snapshots".
/// </summary>
internal static class InlineSnapshotSource
{
	/// <summary>Rewrites the literals <paramref name="edits"/> name. Edits that cannot be applied are reported, the rest are.</summary>
	public static InlineRewrite Rewrite(string source, IReadOnlyList<InlineEdit> edits)
	{
		var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var lexed = Lex(source);
		var errors = new string?[edits.Count];
		var targets = new Target?[edits.Count];
		for (var i = 0; i < edits.Count; i++)
		{
			try
			{
				var target = Locate(source, lexed, edits[i]);
				var current = Decode(source, target);
				if (!SameValue(current, edits[i].Expected))
				{
					throw new InlineSnapshotException(
						$"the snapshot at line {edits[i].Line} no longer holds the value the test ran with; the source changed since. Run the tests again.");
				}

				targets[i] = target;
			}
			catch (InlineSnapshotException e)
			{
				errors[i] = e.Message;
			}
		}

		// One literal can hold one value: cases of a data-driven test that disagree are all refused.
		foreach (var group in Enumerable.Range(0, edits.Count).Where(i => targets[i] is not null).GroupBy(i => (targets[i]!.Start, targets[i]!.End)))
		{
			var values = group.Select(i => edits[i].NewValue).Distinct(StringComparer.Ordinal).Count();
			if (values <= 1)
				continue;
			foreach (var i in group)
			{
				errors[i] = $"{group.Count()} test cases write different values to the snapshot at line {edits[i].Line}; use MatchSnapshot for values that differ per case.";
				targets[i] = null;
			}
		}

		var applied = Enumerable.Range(0, edits.Count)
			.Where(i => targets[i] is not null)
			.GroupBy(i => targets[i]!.Start)
			.Select(g => g.First())
			.OrderByDescending(i => targets[i]!.Start)
			.ToList();
		var result = new StringBuilder(source);
		foreach (var i in applied)
		{
			var target = targets[i]!;
			result.Remove(target.Start, target.End - target.Start);
			result.Insert(target.Start, target.Prefix + Encode(edits[i].NewValue, target.ContentIndent, newline));
		}

		return new InlineRewrite(result.ToString(), errors);
	}

	/// <summary>
	/// Checks that <paramref name="edit"/> can be applied to <paramref name="source"/> without writing anything, and
	/// returns the offset of the literal and, in <paramref name="current"/>, its value. Throws <see cref="InlineSnapshotException"/> when it cannot be located or is
	/// not a plain string literal.
	/// </summary>
	public static int Check(string source, InlineEdit edit, out string? current)
	{
		var target = Locate(source, Lex(source), edit);
		current = Decode(source, target);
		return target.Start;
	}

	/// <summary>The text of a C# file's bytes, and whether it started with a byte order mark.</summary>
	public static string ReadText(byte[] bytes, out bool bom)
	{
		bom = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
		var skip = bom ? Encoding.UTF8.GetPreamble().Length : 0;
		return new UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip);
	}

	/// <summary>A C# file's bytes: UTF-8, with the byte order mark the file had (<c>GetBytes</c> never writes one).</summary>
	public static byte[] ToBytes(string text, bool bom) =>
		bom ? [.. Encoding.UTF8.GetPreamble(), .. new UTF8Encoding(false).GetBytes(text)] : new UTF8Encoding(false).GetBytes(text);

	/// <summary>
	/// Identifies a source file's content, lower case hex. A record carries the hash of the file as the test saw it, so
	/// an edit never lands in a file that changed since: lines shift when an earlier literal grows.
	/// </summary>
	public static string Hash(byte[] bytes) =>
		Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

	/// <summary>Whether a literal's value and a recorded one are the same snapshot: equal once both are normalised.</summary>
	public static bool SameValue(string? current, string? expected) =>
		(current is null && expected is null)
		|| (current is not null && expected is not null
			&& string.Equals(SnapshotText.Normalize(current), SnapshotText.Normalize(expected), StringComparison.Ordinal));

	// ---- encoding ---------------------------------------------------------------------------------------------

	/// <summary>
	/// The C# literal for <paramref name="value"/> (on-disk form: its trailing newline is dropped, <c>Normalize</c> puts
	/// it back): a regular literal for one line, a raw <c>"""</c> literal indented with <paramref name="indent"/> for
	/// several. A multi-line value with characters a raw literal cannot hold safely (controls, line separators) falls
	/// back to a regular literal with escapes.
	/// </summary>
	public static string Encode(string value, string indent, string newline)
	{
		var body = value.TrimEnd('\n');
		if (body.Length == 0)
			return "\"\"";
		if (!body.Contains('\n', StringComparison.Ordinal) || !RawSafe(body))
			return Regular(body);

		var run = 0;
		var longest = 0;
		foreach (var c in body)
		{
			run = c == '"' ? run + 1 : 0;
			longest = Math.Max(longest, run);
		}

		var quotes = new string('"', Math.Max(3, longest + 1));
		var text = new StringBuilder(quotes).Append(newline);
		foreach (var line in body.Split('\n'))
		{
			if (line.Length > 0)
				text.Append(indent).Append(line);
			text.Append(newline);
		}

		return text.Append(indent).Append(quotes).ToString();
	}

	private static bool RawSafe(string body)
	{
		for (var i = 0; i < body.Length; i++)
		{
			var c = body[i];
			if (c is '\n' or '\t')
				continue;
			if (c is < (char)0x20 or (>= (char)0x7F and <= (char)0x9F) or '\u2028' or '\u2029')
				return false;
			if (char.IsHighSurrogate(c) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]))
				i++;
			else if (char.IsSurrogate(c))
				return false;
		}

		return true;
	}

	private static string Regular(string body)
	{
		var text = new StringBuilder("\"");
		for (var i = 0; i < body.Length; i++)
		{
			var c = body[i];
			switch (c)
			{
				case '\\':
					text.Append("\\\\");
					break;
				case '"':
					text.Append("\\\"");
					break;
				case '\n':
					text.Append("\\n");
					break;
				case '\r':
					text.Append("\\r");
					break;
				case '\t':
					text.Append("\\t");
					break;
				case '\0':
					text.Append("\\0");
					break;
				default:
					if (char.IsHighSurrogate(c) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]))
					{
						text.Append(c).Append(body[++i]);
					}
					else if (c < 0x20 || (c >= 0x7F && c <= 0x9F) || c is '\u2028' or '\u2029' || char.IsSurrogate(c))
					{
						text.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
					}
					else
					{
						text.Append(c);
					}

					break;
			}
		}

		return text.Append('"').ToString();
	}

	// ---- decoding ---------------------------------------------------------------------------------------------

	private static string? Decode(string source, Target target)
	{
		if (target.Kind != TargetKind.Literal)
			return null;
		var text = source[target.Start..target.End];
		return target.StringKind switch
		{
			StringKind.Regular => DecodeRegular(text),
			StringKind.Verbatim => text[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal),
			StringKind.Raw => DecodeRaw(text),
			_ => throw new InlineSnapshotException("the expected value is not a plain string literal."),
		};
	}

	private static string DecodeRegular(string literal)
	{
		var body = literal.AsSpan(1, literal.Length - 2);
		var text = new StringBuilder(body.Length);
		for (var i = 0; i < body.Length; i++)
		{
			if (body[i] != '\\')
			{
				text.Append(body[i]);
				continue;
			}

			i++;
			if (i >= body.Length)
				throw new InlineSnapshotException("malformed escape in the string literal.");
			switch (body[i])
			{
				case '\\':
					text.Append('\\');
					break;
				case '"':
					text.Append('"');
					break;
				case '\'':
					text.Append('\'');
					break;
				case '0':
					text.Append('\0');
					break;
				case 'a':
					text.Append('\a');
					break;
				case 'b':
					text.Append('\b');
					break;
				case 'e':
					text.Append('\u001B');
					break;
				case 'f':
					text.Append('\f');
					break;
				case 'n':
					text.Append('\n');
					break;
				case 'r':
					text.Append('\r');
					break;
				case 't':
					text.Append('\t');
					break;
				case 'v':
					text.Append('\v');
					break;
				case 'u':
					i = AppendHex(text, body, i, 4, 4);
					break;
				case 'U':
					i = AppendHex(text, body, i, 8, 8);
					break;
				case 'x':
					i = AppendHex(text, body, i, 1, 4);
					break;
				default:
					throw new InlineSnapshotException($"unknown escape \\{body[i]} in the string literal.");
			}
		}

		return text.ToString();
	}

	private static int AppendHex(StringBuilder text, ReadOnlySpan<char> body, int escape, int min, int max)
	{
		var i = escape + 1;
		var digits = 0;
		var value = 0L;
		while (i < body.Length && digits < max && Uri.IsHexDigit(body[i]))
		{
			value = (value * 16) + Convert.ToInt32(body[i].ToString(), 16);
			i++;
			digits++;
		}

		if (digits < min || value > 0x10FFFF)
			throw new InlineSnapshotException("malformed \\u, \\U or \\x escape in the string literal.");
		if (value <= 0xFFFF)
			text.Append((char)value);
		else if (value is >= 0xD800 and <= 0xDFFF)
			throw new InlineSnapshotException("malformed \\U escape in the string literal.");
		else
			text.Append(char.ConvertFromUtf32((int)value));
		return i - 1;
	}

	private static string DecodeRaw(string literal)
	{
		var quotes = 0;
		while (quotes < literal.Length && literal[quotes] == '"')
			quotes++;
		var content = literal.Substring(quotes, literal.Length - (2 * quotes));
		var lines = SplitLines(content);
		if (lines.Count == 1)
			return content;

		// Multi-line: the opening line and the closing line hold only whitespace; the closing line's is the indent.
		if (lines[0].Trim().Length != 0)
			throw new InlineSnapshotException("malformed raw string literal.");
		var indent = lines[^1];
		if (indent.Trim().Length != 0)
			throw new InlineSnapshotException("malformed raw string literal.");
		var body = new List<string>();
		for (var i = 1; i < lines.Count - 1; i++)
		{
			var line = lines[i];
			if (line.StartsWith(indent, StringComparison.Ordinal))
				body.Add(line[indent.Length..]);
			else if (line.Trim().Length == 0)
				body.Add(string.Empty);
			else
				throw new InlineSnapshotException("malformed raw string literal.");
		}

		return string.Join('\n', body);
	}

	private static List<string> SplitLines(string text)
	{
		var lines = new List<string>();
		var start = 0;
		for (var i = 0; i < text.Length; i++)
		{
			var length = BreakLength(text, i);
			if (length == 0)
				continue;
			lines.Add(text[start..i]);
			i += length - 1;
			start = i + 1;
		}

		lines.Add(text[start..]);
		return lines;
	}

	private static int BreakLength(string text, int i) => text[i] switch
	{
		'\r' => i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1,
		'\n' or '\u0085' or '\u2028' or '\u2029' => 1,
		_ => 0,
	};

	// ---- locating ---------------------------------------------------------------------------------------------

	private enum TargetKind
	{
		/// <summary>A string literal to replace.</summary>
		Literal,

		/// <summary>A <c>null</c> to replace with a literal.</summary>
		Null,

		/// <summary>No arguments: the literal goes right after the opening parenthesis.</summary>
		Insert,

		/// <summary>Other arguments, none for <c>expected</c>: <c>, expected: literal</c> goes after the last.</summary>
		Append,
	}

	private sealed record Target(TargetKind Kind, StringKind StringKind, int Start, int End, string ContentIndent)
	{
		public string Prefix => Kind == TargetKind.Append ? ", expected: " : string.Empty;
	}

	private static Target Locate(string source, Lexed lexed, InlineEdit edit)
	{
		var tokens = lexed.Tokens;
		var calls = new List<Call>();
		for (var i = 0; i < tokens.Count; i++)
		{
			if (tokens[i].Kind == TokenKind.Identifier
				&& string.Equals(Text(source, tokens[i]), edit.MethodName, StringComparison.Ordinal)
				&& ParseCall(source, lexed, i) is { } call)
			{
				calls.Add(call);
			}
		}

		var tight = calls.Where(c => c.IdentLine <= edit.Line && edit.Line <= c.CloseLine).ToList();
		var candidates = tight.Count > 0 ? tight : [.. calls.Where(c => c.StatementLine <= edit.Line && edit.Line <= c.CloseLine)];

		if (edit.Expression is not null)
		{
			var expression = Canonical(edit.Expression);
			candidates = [.. candidates.Where(c => c.Args.Any(a => Canonical(ArgText(source, tokens, a)) == expression))];
		}

		if (candidates.Count == 0)
			throw new InlineSnapshotException($"no call to {edit.MethodName} found at line {edit.Line}; the source changed since the test ran.");
		if (candidates.Count > 1)
			throw new InlineSnapshotException($"{candidates.Count} calls to {edit.MethodName} match line {edit.Line}; put each on its own line.");

		var found = candidates[0];
		var indent = found.CallIndent + (found.CallIndent.Contains('\t', StringComparison.Ordinal) ? "\t" : "    ");
		if (edit.Expression is null)
		{
			return found.Args.Count == 0
				? new Target(TargetKind.Insert, default, found.OpenEnd, found.OpenEnd, indent)
				: new Target(TargetKind.Append, default, tokens[found.Args[^1].Last].End, tokens[found.Args[^1].Last].End, indent);
		}

		var arg = found.Args.First(a => Canonical(ArgText(source, tokens, a)) == Canonical(edit.Expression));
		var first = tokens[arg.ValueFirst];
		if (arg.ValueFirst == arg.Last)
		{
			if (first.Kind == TokenKind.String && first.StringKind != StringKind.Interpolated)
				return new Target(TargetKind.Literal, first.StringKind, first.Start, first.End, indent);
			if (first.Kind == TokenKind.Identifier && Text(source, first) == "null")
				return new Target(TargetKind.Null, default, first.Start, first.End, indent);
		}

		throw new InlineSnapshotException(
			$"the expected value at line {edit.Line} is not a plain string literal ({Canonical(ArgText(source, tokens, arg))}); write it as a literal so snap can update it.");
	}

	private static string ArgText(string source, List<Token> tokens, Arg arg) =>
		source[tokens[arg.ValueFirst].Start..tokens[arg.Last].End];

	private static string Canonical(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

	private static string Text(string source, Token token) => source[token.Start..token.End];

	private sealed record Arg(int ValueFirst, int Last);

	/// <param name="IdentLine"></param>

	/// <param name="CloseLine"></param>
	/// <param name="StatementLine"></param>	/// <param name="CallIndent">The whitespace that starts the line of the method name: a raw literal is indented one level past it.</param>
	/// <param name="OpenEnd"></param>
	/// <param name="Args"></param>
	private sealed record Call(int IdentLine, int CloseLine, int StatementLine, string CallIndent, int OpenEnd, List<Arg> Args);

	/// <summary>The call whose method name is token <paramref name="at"/>, or null when it is not followed by an argument list.</summary>
	private static Call? ParseCall(string source, Lexed lexed, int at)
	{
		var tokens = lexed.Tokens;
		var i = at + 1;
		if (i < tokens.Count && IsPunct(source, tokens[i], '<'))
		{
			var depth = 0;
			for (; i < tokens.Count; i++)
			{
				if (IsPunct(source, tokens[i], '<'))
				{
					depth++;
				}
				else if (IsPunct(source, tokens[i], '>') && --depth == 0)
				{
					i++;
					break;
				}
				else if (IsPunct(source, tokens[i], ';'))
				{
					return null;
				}
			}
		}

		if (i >= tokens.Count || !IsPunct(source, tokens[i], '('))
			return null;

		var open = i;
		var args = new List<Arg>();
		var nesting = 0;
		var argStart = i + 1;
		for (i = open + 1; i < tokens.Count; i++)
		{
			var token = tokens[i];
			if (token.Kind != TokenKind.Punctuation)
				continue;
			var c = source[token.Start];
			if (c is '(' or '[' or '{')
			{
				nesting++;
			}
			else if (c is ')' && nesting == 0)
			{
				if (i > argStart)
					args.Add(MakeArg(source, tokens, argStart, i - 1));
				var statement = StatementStart(source, tokens, at);
				var line = lexed.LineOf(tokens[statement].Start);
				var callLine = lexed.LineOf(tokens[at].Start);
				return new Call(callLine, lexed.LineOf(token.Start), line, IndentOf(source, lexed, callLine), tokens[open].End, args);
			}
			else if (c is ')' or ']' or '}')
			{
				nesting--;
			}
			else if (c == ',' && nesting == 0)
			{
				args.Add(MakeArg(source, tokens, argStart, i - 1));
				argStart = i + 1;
			}
		}

		return null;
	}

	private static Arg MakeArg(string source, List<Token> tokens, int first, int last)
	{
		var named = last > first
			&& tokens[first].Kind == TokenKind.Identifier
			&& IsPunct(source, tokens[first + 1], ':')
			&& !(first + 2 <= last && IsPunct(source, tokens[first + 2], ':'));
		return new Arg(named ? first + 2 : first, last);
	}

	private static int StatementStart(string source, List<Token> tokens, int at)
	{
		var i = at;
		while (i > 0)
		{
			var previous = tokens[i - 1];
			if (previous.Kind == TokenKind.Punctuation && source[previous.Start] is ';' or '{' or '}')
				break;
			i--;
		}

		return i;
	}

	private static string IndentOf(string source, Lexed lexed, int line)
	{
		var start = lexed.LineStarts[line - 1];
		var end = start;
		while (end < source.Length && source[end] is ' ' or '\t')
			end++;
		return source[start..end];
	}

	private static bool IsPunct(string source, Token token, char c) => token.Kind == TokenKind.Punctuation && source[token.Start] == c;

	// ---- lexing -----------------------------------------------------------------------------------------------

	private enum TokenKind { Identifier, Punctuation, String, Other }

	private enum StringKind { Regular, Verbatim, Raw, Interpolated }

	private readonly record struct Token(TokenKind Kind, int Start, int End, StringKind StringKind = default);

	private sealed class Lexed(List<Token> tokens, List<int> lineStarts)
	{
		public List<Token> Tokens { get; } = tokens;

		public List<int> LineStarts { get; } = lineStarts;

		/// <summary>The 1-based line of a source offset.</summary>
		public int LineOf(int offset)
		{
			var index = LineStarts.BinarySearch(offset);
			return index >= 0 ? index + 1 : ~index;
		}
	}

	private static Lexed Lex(string source)
	{
		var lineStarts = new List<int> { 0 };
		for (var i = 0; i < source.Length; i++)
		{
			var length = BreakLength(source, i);
			if (length == 0)
				continue;
			i += length - 1;
			lineStarts.Add(i + 1);
		}

		var tokens = new List<Token>();
		var n = source.Length;
		var lineStart = true;
		for (var i = 0; i < n;)
		{
			var c = source[i];
			if (BreakLength(source, i) is var br and > 0)
			{
				i += br;
				lineStart = true;
				continue;
			}

			if (char.IsWhiteSpace(c))
			{
				i++;
				continue;
			}

			var wasLineStart = lineStart;
			lineStart = false;
			if (c == '/' && i + 1 < n && source[i + 1] == '/')
			{
				while (i < n && BreakLength(source, i) == 0)
					i++;
				continue;
			}

			if (c == '/' && i + 1 < n && source[i + 1] == '*')
			{
				var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
				i = end < 0 ? n : end + 2;
				continue;
			}

			if (c == '#' && wasLineStart)
			{
				while (i < n && BreakLength(source, i) == 0)
					i++;
				continue;
			}

			if (c == '\'')
			{
				var start = i++;
				while (i < n && source[i] != '\'')
					i += source[i] == '\\' ? 2 : 1;
				i = Math.Min(n, i + 1);
				tokens.Add(new Token(TokenKind.Other, start, i));
				continue;
			}

			if (TryString(source, i, out var stringEnd, out var kind))
			{
				tokens.Add(new Token(TokenKind.String, i, stringEnd, kind));
				i = stringEnd;
				continue;
			}

			if (char.IsLetter(c) || c == '_' || (c == '@' && i + 1 < n && (char.IsLetter(source[i + 1]) || source[i + 1] == '_')))
			{
				var start = i;
				i++;
				while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
					i++;
				tokens.Add(new Token(TokenKind.Identifier, start, i));
				continue;
			}

			if (char.IsDigit(c))
			{
				var start = i;
				while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] is '.' or '_'))
					i++;
				tokens.Add(new Token(TokenKind.Other, start, i));
				continue;
			}

			tokens.Add(new Token(TokenKind.Punctuation, i, i + 1));
			i++;
		}

		return new Lexed(tokens, lineStarts);
	}

	/// <summary>Reads a string literal starting at <paramref name="i"/>, if one starts there.</summary>
	private static bool TryString(string s, int i, out int end, out StringKind kind)
	{
		end = i;
		kind = StringKind.Regular;
		var n = s.Length;
		var p = i;
		var dollars = 0;
		var verbatim = false;
		while (p < n && (s[p] == '$' || s[p] == '@') && p - i < 8)
		{
			if (s[p] == '$')
				dollars++;
			else
				verbatim = true;
			p++;
		}

		if (p >= n || s[p] != '"' || (p > i && dollars == 0 && !verbatim))
			return false;
		// `@` alone in front of a letter is an identifier, handled by the caller; here a quote follows.
		var quotes = 0;
		while (p + quotes < n && s[p + quotes] == '"')
			quotes++;

		if (quotes >= 3 && !verbatim)
		{
			// Raw: closed by a run of exactly as many quotes. Interpolation holes are not parsed; a hole holding
			// the closing quotes would end it early, which is vanishingly rare in a snapshot.
			var q = p + quotes;
			while (q < n)
			{
				if (s[q] != '"')
				{
					q++;
					continue;
				}

				var run = 0;
				while (q + run < n && s[q + run] == '"')
					run++;
				if (run >= quotes)
				{
					end = q + run;
					kind = dollars > 0 ? StringKind.Interpolated : StringKind.Raw;
					return true;
				}

				q += run;
			}

			throw new InlineSnapshotException("unterminated raw string literal in the source.");
		}

		var interpolated = dollars > 0;
		var position = p + 1;
		while (position < n)
		{
			var c = s[position];
			if (c == '"')
			{
				if (verbatim && position + 1 < n && s[position + 1] == '"')
				{
					position += 2;
					continue;
				}

				end = position + 1;
				kind = interpolated ? StringKind.Interpolated : verbatim ? StringKind.Verbatim : StringKind.Regular;
				return true;
			}

			if (c == '\\' && !verbatim)
			{
				position += 2;
				continue;
			}

			if (!verbatim && BreakLength(s, position) > 0)
				break;

			if (interpolated && c == '{')
			{
				if (position + 1 < n && s[position + 1] == '{')
				{
					position += 2;
					continue;
				}

				position = SkipHole(s, position + 1);
				continue;
			}

			position++;
		}

		throw new InlineSnapshotException("unterminated string literal in the source.");
	}

	/// <summary>The offset after the <c>}</c> that closes an interpolation hole, skipping strings inside it.</summary>
	private static int SkipHole(string s, int position)
	{
		var depth = 1;
		while (position < s.Length)
		{
			var c = s[position];
			if (c == '"' && TryString(s, position, out var end, out _))
			{
				position = end;
				continue;
			}

			if (c == '{')
				depth++;
			else if (c == '}' && --depth == 0)
				return position + 1;
			position++;
		}

		throw new InlineSnapshotException("unterminated interpolation in the source.");
	}
}
