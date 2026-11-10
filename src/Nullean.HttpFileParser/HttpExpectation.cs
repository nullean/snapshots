using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Nullean.HttpFileParser;

/// <summary>
/// A check on a response, written in a comment before the request:
/// <c># @expect status 200</c>, <c># @expect header content-type ~ json</c> or
/// <c># @expect json $.items[*].name contains "lamp"</c>.
/// </summary>
public abstract class HttpExpectation
{
	private protected HttpExpectation(int line, string text)
	{
		Line = line;
		Text = text;
	}

	/// <summary>The 1-based line of the directive.</summary>
	public int Line { get; }

	/// <summary>The directive as written, without the comment marker: <c>@expect status 200</c>.</summary>
	public string Text { get; }

	/// <summary>Checks <paramref name="response"/>; <see langword="null"/> when it holds, else why it does not.</summary>
	public abstract string? Check(HttpFileResponse response);

	/// <summary>Reads the text after <c>@expect</c>. <see langword="false"/> with the problem when it is not an expectation.</summary>
	internal static bool TryParse(string argument, int line, out HttpExpectation? expectation, out string problem)
	{
		expectation = null;
		var text = "@expect " + argument;
		var (kind, rest) = SplitWord(argument);
		switch (kind)
		{
			case "status":
				return HttpStatusExpectation.TryParse(rest, line, text, out expectation, out problem);
			case "header":
				return HttpHeaderExpectation.TryParse(rest, line, text, out expectation, out problem);
			case "json":
				return HttpJsonExpectation.TryParse(rest, line, text, out expectation, out problem);
			default:
				problem = $"'@expect {argument}' needs a kind: '@expect status 200', '@expect header content-type ~ json' or '@expect json $.id equals 1'";
				return false;
		}
	}

	internal static (string Word, string Remainder) SplitWord(string text)
	{
		text = text.Trim();
		var space = text.IndexOfAny([' ', '\t']);
		return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
	}

	/// <summary>A value after an operator: a JSON string literal, or the rest of the line as written.</summary>
	internal static bool TryParseValue(string text, out string value)
	{
		value = text.Trim();
		if (value.Length == 0)
			return false;
		if (value[0] != '"')
			return true;
		try
		{
			using var document = JsonDocument.Parse(value);
			if (document.RootElement.ValueKind != JsonValueKind.String)
				return false;
			value = document.RootElement.GetString() ?? string.Empty;
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	internal static string Quote(string value) =>
		"\"" + (value.Length > 80 ? value[..80] + "…" : value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

internal sealed class HttpStatusExpectation : HttpExpectation
{
	private readonly int _code;
	private readonly bool _wholeClass;

	private HttpStatusExpectation(int line, string text, int code, bool wholeClass) : base(line, text)
	{
		_code = code;
		_wholeClass = wholeClass;
	}

	public static bool TryParse(string rest, int line, string text, out HttpExpectation? expectation, out string problem)
	{
		expectation = null;
		problem = $"'{text}' needs a status code or a class: '@expect status 200' or '@expect status 2xx'";
		var word = rest.Trim();
		if (word.Length == 3 && char.IsAsciiDigit(word[0]) && word[1..].Equals("xx", StringComparison.OrdinalIgnoreCase))
		{
			expectation = new HttpStatusExpectation(line, text, word[0] - '0', wholeClass: true);
			return word[0] is >= '1' and <= '5';
		}
		if (word.Length == 3 && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 100 and <= 599)
		{
			expectation = new HttpStatusExpectation(line, text, code, wholeClass: false);
			return true;
		}
		return false;
	}

	public override string? Check(HttpFileResponse response)
	{
		var holds = _wholeClass ? response.StatusCode / 100 == _code : response.StatusCode == _code;
		return holds ? null : $"{Text} failed: the response was {response.StatusCode} {response.ReasonPhrase}".TrimEnd();
	}
}

internal sealed class HttpHeaderExpectation : HttpExpectation
{
	private readonly string _name;
	private readonly string _contains;

	private HttpHeaderExpectation(int line, string text, string name, string contains) : base(line, text)
	{
		_name = name;
		_contains = contains;
	}

	public static bool TryParse(string rest, int line, string text, out HttpExpectation? expectation, out string problem)
	{
		expectation = null;
		problem = $"'{text}' needs a header name, '~' and the text it contains: '@expect header content-type ~ json'";
		var (name, afterName) = SplitWord(rest);
		var (op, afterOp) = SplitWord(afterName);
		if (name.Length == 0 || op != "~" || !TryParseValue(afterOp, out var contains))
			return false;
		expectation = new HttpHeaderExpectation(line, text, name, contains);
		return true;
	}

	public override string? Check(HttpFileResponse response)
	{
		var values = response.GetHeaderValues(_name).ToList();
		if (values.Count == 0)
			return $"{Text} failed: the response has no {_name} header";
		if (values.Any(v => v.Contains(_contains, StringComparison.OrdinalIgnoreCase)))
			return null;
		return $"{Text} failed: {_name} was {string.Join(", ", values.Select(Quote))}";
	}
}

internal sealed class HttpJsonExpectation : HttpExpectation
{
	private const int ShownValues = 5;

	private readonly JsonPath _path;
	private readonly bool _equals;
	private readonly string _expected;

	private HttpJsonExpectation(int line, string text, JsonPath path, bool equals, string expected) : base(line, text)
	{
		_path = path;
		_equals = equals;
		_expected = expected;
	}

	public static bool TryParse(string rest, int line, string text, out HttpExpectation? expectation, out string problem)
	{
		expectation = null;
		problem = $"'{text}' needs a JSONPath, 'equals' or 'contains', and a value: '@expect json $.items[*].name contains \"lamp\"'";
		var (pathText, afterPath) = SplitPath(rest);
		var (op, afterOp) = SplitWord(afterPath);
		if (op is not ("equals" or "contains") || !TryParseValue(afterOp, out var expected))
			return false;
		if (!JsonPath.TryParse(pathText, out var path, out var pathProblem, allowWildcard: true))
		{
			problem = $"'{text}': {pathProblem}";
			return false;
		}
		expectation = new HttpJsonExpectation(line, text, path!, op == "equals", expected);
		return true;
	}

	/// <summary>The path runs to the first space outside brackets and quotes, so <c>$["a b"]</c> is one path.</summary>
	private static (string Path, string Remainder) SplitPath(string text)
	{
		text = text.Trim();
		var depth = 0;
		char quote = '\0';
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (quote != '\0')
				quote = c == quote ? '\0' : quote;
			else if (c is '"' or '\'')
				quote = c;
			else if (c == '[')
				depth++;
			else if (c == ']')
				depth--;
			else if (char.IsWhiteSpace(c) && depth <= 0)
				return (text[..i], text[(i + 1)..].Trim());
		}
		return (text, string.Empty);
	}

	public override string? Check(HttpFileResponse response)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(response.Body);
		}
		catch (JsonException)
		{
			return $"{Text} failed: the body is not JSON{(response.MediaType is { } type ? $" ({type})" : string.Empty)}";
		}

		using (document)
		{
			var selected = _path.SelectAll(document.RootElement);
			if (selected.Count == 0)
				return $"{Text} failed: {_path.Text} selects nothing";
			var texts = selected.Select(e => (Element: e, Text: JsonPath.ToText(e))).ToList();
			// equals: every selected value must equal it; contains: one selected value is enough.
			var holds = _equals
				? texts.All(t => t.Text == _expected)
				: texts.Any(t => Contains(t.Element, t.Text, _expected));
			return holds ? null : $"{Text} failed: {_path.Text} selected {Describe([.. texts.Select(t => t.Text)])}";
		}
	}

	private static bool Contains(JsonElement element, string text, string expected) =>
		element.ValueKind == JsonValueKind.Array
			? element.EnumerateArray().Any(e => JsonPath.ToText(e) == expected)
			: text.Contains(expected, StringComparison.Ordinal);

	private static string Describe(List<string> values)
	{
		var shown = new StringBuilder(string.Join(", ", values.Take(ShownValues).Select(Quote)));
		if (values.Count > ShownValues)
			shown.Append(CultureInfo.InvariantCulture, $", and {values.Count - ShownValues} more");
		return shown.ToString();
	}
}
