using System.Globalization;
using System.Text;

namespace Nullean.HttpFileParser;

/// <summary>Text with <c>{{ }}</c> references, split into literal text and parsed expressions.</summary>
internal sealed class Template
{
	private Template(string text, IReadOnlyList<object> segments)
	{
		Text = text;
		Segments = segments;
	}

	public static Template Empty { get; } = new("", []);

	/// <summary>The text as written.</summary>
	public string Text { get; }

	/// <summary>Each segment is a literal <see cref="string"/> or an <see cref="Expr"/>.</summary>
	public IReadOnlyList<object> Segments { get; }

	public IEnumerable<Expr> Expressions => Segments.OfType<Expr>();

	/// <summary>
	/// Splits <paramref name="text"/> at <c>{{ }}</c>. An unterminated <c>{{</c> is literal text. Problems inside a reference are
	/// added to <paramref name="errors"/> with the line the reference is on.
	/// </summary>
	public static Template Parse(string text, int line, string path, List<HttpFileError> errors, bool countLines = true)
	{
		var segments = new List<object>();
		var literal = new StringBuilder();
		var i = 0;
		var currentLine = line;
		while (i < text.Length)
		{
			var open = text.IndexOf("{{", i, StringComparison.Ordinal);
			if (open < 0)
				break;
			var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
			if (close < 0)
				break;
			// "{{ {{a}}": the reference is the innermost "{{" before the "}}"; the first one is literal.
			open = text.LastIndexOf("{{", close - 1, close - open, StringComparison.Ordinal);
			// A reference never spans lines: "{{" with no "}}" on its line is literal text.
			if (text.AsSpan(open, close - open).Contains('\n'))
			{
				var skipped = text[i..(open + 2)];
				literal.Append(skipped);
				if (countLines)
					currentLine += Count(skipped, '\n');
				i = open + 2;
				continue;
			}
			var before = text[i..open];
			literal.Append(before);
			if (countLines)
				currentLine += Count(before, '\n');
			if (literal.Length > 0)
			{
				segments.Add(literal.ToString());
				literal.Clear();
			}
			var inner = text[(open + 2)..close];
			segments.Add(Expr.Parse(inner, currentLine, path, errors));
			i = close + 2;
		}
		literal.Append(text, i, text.Length - i);
		if (literal.Length > 0)
			segments.Add(literal.ToString());
		return new Template(text, segments);
	}

	/// <summary>Joins templates parsed from separate lines (a URL and its continuation lines) into one.</summary>
	public static Template Concat(IReadOnlyList<Template> parts) =>
		parts.Count == 1 ? parts[0] : new Template(string.Concat(parts.Select(p => p.Text)), [.. parts.SelectMany(p => p.Segments)]);

	public string Render(Func<Expr, string> resolve)
	{
		var sb = new StringBuilder();
		foreach (var segment in Segments)
		{
			if (segment is Expr expr)
				sb.Append(resolve(expr));
			else
				sb.Append((string)segment);
		}
		return sb.ToString();
	}

	private static int Count(string s, char c)
	{
		var n = 0;
		foreach (var ch in s)
		{
			if (ch == c)
				n++;
		}
		return n;
	}
}

/// <summary>A parsed <c>{{ }}</c> reference.</summary>
internal abstract class Expr
{
	protected Expr(string text, int line)
	{
		Text = text;
		Line = line;
	}

	/// <summary>The trimmed text between the braces.</summary>
	public string Text { get; }

	public int Line { get; }

	/// <summary>The reference as written, braces included: what redaction puts back.</summary>
	public string Reference => "{{" + Text + "}}";

	public static Expr Parse(string inner, int line, string path, List<HttpFileError> errors)
	{
		var text = inner.Trim();
		if (text.Length == 0)
			return Fail(text, line, path, errors, "empty variable reference '{{}}'");
		if (text[0] == '$')
			return DynamicExpr.Parse(text, line, path, errors);
		if (RequestReferenceExpr.TryParse(text, line, path, errors, out var reference))
			return reference;
		foreach (var ch in text)
		{
			if (char.IsWhiteSpace(ch) || ch is '{' or '}')
				return Fail(text, line, path, errors, $"invalid variable reference '{{{{{text}}}}}': a variable name cannot contain spaces or braces");
		}
		return new VariableExpr(text, line);
	}

	private static VariableExpr Fail(string text, int line, string path, List<HttpFileError> errors, string message)
	{
		errors.Add(new HttpFileError(path, line, message));
		return new VariableExpr(text, line);
	}
}

/// <summary><c>{{name}}</c>: a file, environment or override variable.</summary>
internal sealed class VariableExpr(string text, int line) : Expr(text, line)
{
	public string Name => Text;
}

/// <summary><c>{{login.response.body.$.token}}</c>, <c>{{login.request.headers.Accept}}</c>.</summary>
internal sealed class RequestReferenceExpr : Expr
{
	private RequestReferenceExpr(string text, int line, string requestName, bool response, bool body, string selector, JsonPath? jsonPath) : base(text, line)
	{
		RequestName = requestName;
		Response = response;
		Body = body;
		Selector = selector;
		JsonPath = jsonPath;
	}

	public string RequestName { get; }

	/// <summary><see langword="true"/> for <c>response</c>, <see langword="false"/> for <c>request</c>.</summary>
	public bool Response { get; }

	/// <summary><see langword="true"/> for <c>body</c>, <see langword="false"/> for <c>headers</c>.</summary>
	public bool Body { get; }

	/// <summary><c>*</c>, a JSONPath, or a header name.</summary>
	public string Selector { get; }

	public JsonPath? JsonPath { get; }

	public static bool TryParse(string text, int line, string path, List<HttpFileError> errors, out RequestReferenceExpr reference)
	{
		reference = null!;
		var first = text.IndexOf('.', StringComparison.Ordinal);
		if (first <= 0)
			return false;
		var rest = text[(first + 1)..];
		bool response;
		if (rest.StartsWith("response.", StringComparison.Ordinal))
			response = true;
		else if (rest.StartsWith("request.", StringComparison.Ordinal))
			response = false;
		else
			return false;
		rest = rest[(rest.IndexOf('.', StringComparison.Ordinal) + 1)..];
		bool body;
		if (rest == "body" || rest.StartsWith("body.", StringComparison.Ordinal))
			body = true;
		else if (rest.StartsWith("headers.", StringComparison.Ordinal))
			body = false;
		else
			return false;
		var selector = rest == "body" ? "*" : rest[(rest.IndexOf('.', StringComparison.Ordinal) + 1)..];
		var name = text[..first];
		JsonPath? jsonPath = null;
		if (selector.Length == 0)
		{
			errors.Add(new HttpFileError(path, line, $"'{{{{{text}}}}}' needs a selector after '{(body ? "body" : "headers")}.'"));
		}
		else if (body && selector.StartsWith('/'))
		{
			errors.Add(
				new HttpFileError(
					path,
					line,
					$"XPath ('{selector}') in '{{{{{text}}}}}' is not supported; use JSONPath ('$.name') for JSON bodies or '*' for the whole body"
				)
			);
		}
		else if (body && selector != "*")
		{
			if (!selector.StartsWith('$'))
				errors.Add(
					new HttpFileError(
						path,
						line,
						$"'{selector}' in '{{{{{text}}}}}' is not a body selector: use '*' for the whole body or a JSONPath starting with '$'"
					)
				);
			else if (!JsonPath.TryParse(selector, out jsonPath, out var problem))
				errors.Add(new HttpFileError(path, line, $"invalid JSONPath '{selector}' in '{{{{{text}}}}}': {problem}"));
		}
		reference = new RequestReferenceExpr(text, line, name, response, body, selector, jsonPath);
		return true;
	}
}

internal enum DynamicKind
{
	Guid,
	RandomInt,
	Timestamp,
	IsoTimestamp,
	DateTime,
	LocalDateTime,
	ProcessEnv,
	DotEnv,
	Secret,
}

/// <summary><c>{{$guid}}</c>, <c>{{$datetime iso8601 1 d}}</c>, <c>{{$processEnv NAME}}</c> and the other dynamic values.</summary>
internal sealed class DynamicExpr : Expr
{
	private DynamicExpr(string text, int line) : base(text, line)
	{ }

	public DynamicKind Kind { get; private init; }

	/// <summary>The variable name for <c>$processEnv</c>, <c>$env.</c>, <c>$dotenv</c> and <c>$secret</c>.</summary>
	public string Argument { get; private init; } = "";

	/// <summary><c>$processEnv %name</c>: the environment variable's name is the value of the variable <c>name</c>.</summary>
	public bool Indirect { get; private init; }

	public int Min { get; private init; }

	public int Max { get; private init; } = int.MaxValue;

	/// <summary><c>rfc1123</c>, <c>iso8601</c> or a custom .NET format; <see langword="null"/> means iso8601.</summary>
	public string? Format { get; private init; }

	public int OffsetAmount { get; private init; }

	public string? OffsetUnit { get; private init; }

	private static readonly string[] Units = ["ms", "s", "m", "h", "d", "w", "M", "y"];

	public static new Expr Parse(string text, int line, string path, List<HttpFileError> errors)
	{
		var tokens = Tokenize(text, out var tokenError);
		var head = tokens.Count > 0 ? tokens[0].Value : text;
		var args = tokens.Skip(1).ToList();

		void Error(string message) => errors.Add(new HttpFileError(path, line, message));

		if (tokenError is not null)
		{
			Error($"'{{{{{text}}}}}': {tokenError}");
			return new DynamicExpr(text, line) { Kind = DynamicKind.Guid };
		}

		switch (head)
		{
			case "$guid" or "$uuid" or "$random.uuid":
				NoArguments(head, args, Error);
				return new DynamicExpr(text, line) { Kind = DynamicKind.Guid };
			case "$isoTimestamp":
				NoArguments(head, args, Error);
				return new DynamicExpr(text, line) { Kind = DynamicKind.IsoTimestamp };
			case "$randomInt":
				{
					if (args.Count == 0)
						return new DynamicExpr(text, line) { Kind = DynamicKind.RandomInt };
					if (args.Count != 2 || !TryInt(args[0].Value, out var min) || !TryInt(args[1].Value, out var max))
					{
						Error($"'{{{{{text}}}}}': $randomInt takes no arguments or two integers, '$randomInt min max'");
						return new DynamicExpr(text, line) { Kind = DynamicKind.RandomInt };
					}
					if (min >= max)
						Error($"'{{{{{text}}}}}': $randomInt needs min below max (max is exclusive)");
					return new DynamicExpr(text, line) { Kind = DynamicKind.RandomInt, Min = min, Max = Math.Max(min + 1, max) };
				}
			case "$timestamp":
				{
					var (amount, unit) = Offset(head, args, 0, Error);
					return new DynamicExpr(text, line) { Kind = DynamicKind.Timestamp, OffsetAmount = amount, OffsetUnit = unit };
				}
			case "$datetime" or "$localDatetime":
				{
					string? format = null;
					var start = 0;
					if (args.Count > 0 && (args[0].Quoted || !TryInt(args[0].Value, out _)) && !IsCompactOffset(args[0]))
					{
						format = args[0].Value;
						start = 1;
						if (!args[0].Quoted && format is not ("rfc1123" or "iso8601"))
							Error($"'{{{{{text}}}}}': the format must be rfc1123, iso8601 or a custom format in quotes, not '{format}'");
						else if (args[0].Quoted)
							CheckCustomFormat(format, text, Error);
					}
					var (amount, unit) = Offset(head, args, start, Error);
					return new DynamicExpr(text, line)
					{
						Kind = head == "$datetime" ? DynamicKind.DateTime : DynamicKind.LocalDateTime,
						Format = format,
						OffsetAmount = amount,
						OffsetUnit = unit,
					};
				}
			case "$processEnv" or "$dotenv" or "$secret":
				{
					if (args.Count != 1 || args[0].Value.Length == 0 || args[0].Value == "%")
					{
						Error($"'{{{{{text}}}}}': {head} takes one name, '{head} NAME'");
						return new DynamicExpr(text, line) { Kind = DynamicKind.Guid };
					}
					var name = args[0].Value;
					var indirect = head != "$secret" && name.StartsWith('%');
					return new DynamicExpr(text, line)
					{
						Kind = head switch
						{
							"$processEnv" => DynamicKind.ProcessEnv,
							"$dotenv" => DynamicKind.DotEnv,
							_ => DynamicKind.Secret
						},
						Argument = indirect ? name[1..] : name,
						Indirect = indirect,
					};
				}
			case "$aadToken" or "$aadV2Token":
				Error(
					$"'{{{{{text}}}}}' is not supported: Azure AD tokens need an interactive sign-in. Provide the token through HttpFileRunOptions.Secrets and reference it as '{{{{$secret Name}}}}'"
				);
				return new DynamicExpr(text, line) { Kind = DynamicKind.Guid };
			default:
				if (head.StartsWith("$env.", StringComparison.Ordinal) && head.Length > 5)
				{
					NoArguments(head, args, Error);
					return new DynamicExpr(text, line) { Kind = DynamicKind.ProcessEnv, Argument = head[5..] };
				}
				Error($"unknown dynamic variable '{head}' in '{{{{{text}}}}}'");
				return new DynamicExpr(text, line) { Kind = DynamicKind.Guid };
		}
	}

	private static void NoArguments(string head, List<Token> args, Action<string> error)
	{
		if (args.Count > 0)
			error($"{head} takes no arguments");
	}

	private static bool TryInt(string s, out int value) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

	private static bool IsCompactOffset(Token token) => !token.Quoted && SplitCompactOffset(token.Value, out _, out _);

	private static bool SplitCompactOffset(string s, out int amount, out string unit)
	{
		amount = 0;
		unit = "";
		foreach (var u in Units.OrderByDescending(u => u.Length))
		{
			if (s.Length > u.Length && s.EndsWith(u, StringComparison.Ordinal) && TryInt(s[..^u.Length], out amount))
			{
				unit = u;
				return true;
			}
		}
		return false;
	}

	private static (int Amount, string? Unit) Offset(string head, List<Token> args, int start, Action<string> error)
	{
		var rest = args.Skip(start).ToList();
		if (rest.Count == 0)
			return (0, null);
		if (rest.Count == 1 && SplitCompactOffset(rest[0].Value, out var compact, out var compactUnit))
			return (compact, compactUnit);
		if (rest.Count == 2 && TryInt(rest[0].Value, out var amount) && Units.Contains(rest[1].Value, StringComparer.Ordinal))
			return (amount, rest[1].Value);
		error($"{head}: expected an offset like '1 d' or '-3 h' (units ms, s, m, h, d, w, M, y), got '{string.Join(" ", rest.Select(t => t.Value))}'");
		return (0, null);
	}

	private static void CheckCustomFormat(string format, string text, Action<string> error)
	{
		try
		{
			_ = DateTimeOffset.UnixEpoch.ToString(format, CultureInfo.InvariantCulture);
		}
		catch (FormatException)
		{
			error($"'{{{{{text}}}}}': '{format}' is not a valid .NET date and time format");
		}
	}

	private readonly record struct Token(string Value, bool Quoted);

	private static List<Token> Tokenize(string text, out string? error)
	{
		error = null;
		var tokens = new List<Token>();
		var i = 0;
		while (i < text.Length)
		{
			if (char.IsWhiteSpace(text[i]))
			{
				i++;
				continue;
			}
			if (text[i] is '"' or '\'')
			{
				var quote = text[i];
				var end = text.IndexOf(quote, i + 1);
				if (end < 0)
				{
					error = $"unterminated {quote}";
					return tokens;
				}
				tokens.Add(new Token(text[(i + 1)..end], true));
				i = end + 1;
				continue;
			}
			var start = i;
			while (i < text.Length && !char.IsWhiteSpace(text[i]))
				i++;
			tokens.Add(new Token(text[start..i], false));
		}
		return tokens;
	}

	/// <summary>Applies the offset (<c>-1 y</c>) to <paramref name="time"/>.</summary>
	public DateTimeOffset ApplyOffset(DateTimeOffset time) => OffsetUnit switch
	{
		"ms" => time.AddMilliseconds(OffsetAmount),
		"s" => time.AddSeconds(OffsetAmount),
		"m" => time.AddMinutes(OffsetAmount),
		"h" => time.AddHours(OffsetAmount),
		"d" => time.AddDays(OffsetAmount),
		"w" => time.AddDays(7.0 * OffsetAmount),
		"M" => time.AddMonths(OffsetAmount),
		"y" => time.AddYears(OffsetAmount),
		_ => time,
	};
}
