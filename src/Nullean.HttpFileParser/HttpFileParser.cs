using System.Globalization;
using System.Text;

namespace Nullean.HttpFileParser;

/// <summary>Turns the text of a <c>.http</c> file into requests and variables, collecting every problem.</summary>
internal sealed class HttpFileParser
{
	private readonly string _path;
	private readonly string _directory;
	private readonly string[] _lines;
	private readonly List<HttpFileError> _errors = [];
	private readonly List<HttpFileRequest> _requests = [];
	private readonly List<HttpFileVariable> _variables = [];

	private HttpFileParser(string text, string path)
	{
		_path = path;
		_directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetFullPath(".");
		_lines = text.Split('\n');
		for (var i = 0; i < _lines.Length; i++)
			_lines[i] = _lines[i].TrimEnd('\r');
	}

	public static HttpFile Parse(string text, string path)
	{
		var parser = new HttpFileParser(text, path);
		parser.Run();
		if (parser._errors.Count > 0)
			throw new HttpFileParseException([.. parser._errors.OrderBy(e => e.Line)]);
		return new HttpFile(path, parser._requests, parser._variables);
	}

	/// <summary>A line of three or more <c>#</c>: the request separator, optionally followed by the next request's name.</summary>
	internal static bool IsSeparator(string line, out string? name)
	{
		name = null;
		var t = line.TrimStart();
		if (!t.StartsWith("###", StringComparison.Ordinal))
			return false;
		var rest = t.TrimStart('#').Trim();
		name = rest.Length > 0 ? rest : null;
		return true;
	}

	private void Run()
	{
		var start = 0;
		string? separatorName = null;
		for (var i = 0; i <= _lines.Length; i++)
		{
			string? nextName = null;
			if (i < _lines.Length && !IsSeparator(_lines[i], out nextName))
				continue;
			ParseBlock(start, i, separatorName);
			start = i + 1;
			separatorName = nextName;
		}
	}

	private void Error(int index, string message) => _errors.Add(new HttpFileError(_path, index + 1, message));

	private static bool IsComment(string trimmed) => trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal);

	private static string CommentText(string trimmed) =>
		(trimmed.StartsWith('#') ? trimmed.TrimStart('#') : trimmed[2..].TrimStart('/')).Trim();

	/// <summary>Parses lines [<paramref name="start"/>, <paramref name="end"/>): comments, variables, then at most one request.</summary>
	private void ParseBlock(int start, int end, string? separatorName)
	{
		string? name = null;
		var nameLine = -1;
		var noRedirect = false;
		var noCookieJar = false;
		TimeSpan? timeout = null;
		var hasDirective = false;
		var k = start;
		for (; k < end; k++)
		{
			var t = _lines[k].Trim();
			if (t.Length == 0)
				continue;
			if (t.StartsWith("< {%", StringComparison.Ordinal))
			{
				Error(k, "JetBrains pre-request scripts ('< {% ... %}') are not supported: they run JavaScript");
				k = SkipScript(k, end);
				continue;
			}
			if (t.StartsWith('<'))
			{
				Error(k, "JetBrains pre-request script files ('< script.js') are not supported: they run JavaScript");
				continue;
			}
			if (IsComment(t))
			{
				var c = CommentText(t);
				if (!c.StartsWith('@'))
					continue;
				var word = new string([.. c.Skip(1).TakeWhile(ch => !char.IsWhiteSpace(ch) && ch != '=')]);
				var argument = c[(1 + word.Length)..].Trim();
				switch (word)
				{
					case "name":
						if (argument.StartsWith('='))
							argument = argument[1..].Trim();
						if (argument.Length == 0)
						{
							Error(k, "'@name' needs a name, e.g. '# @name login'");
						}
						else if (argument.Any(char.IsWhiteSpace) || argument.Contains('.', StringComparison.Ordinal))
						{
							Error(k, $"request name '{argument}' cannot contain spaces or dots");
						}
						else
						{
							name = argument;
							nameLine = k;
						}
						break;
					case "no-redirect":
						noRedirect = true;
						hasDirective = true;
						break;
					case "no-cookie-jar":
						noCookieJar = true;
						hasDirective = true;
						break;
					case "timeout":
						hasDirective = true;
						if (TryParseTimeout(argument, out var value))
							timeout = value;
						else
							Error(k, $"'@timeout {argument}' needs a positive number of seconds, optionally with a unit: '@timeout 30', '@timeout 500 ms', '@timeout 2 m'");
						break;
					case "prompt":
						Error(k, "prompt variables ('@prompt') are not supported: a run cannot ask questions. Pass the value with HttpFileRunOptions.Variables");
						break;
					default:
						// Unknown directives (@note, @no-log, @connection-timeout, ...) are plain comments.
						break;
				}
				continue;
			}
			if (t.StartsWith('@'))
			{
				ParseVariable(k, t);
				continue;
			}
			if (t.StartsWith("import ", StringComparison.Ordinal) || t.StartsWith("run ", StringComparison.Ordinal))
			{
				Error(k, $"JetBrains '{t.Split(' ')[0]}' statements are not supported");
				continue;
			}
			break;
		}

		if (k >= end)
		{
			if (nameLine >= 0)
				Error(nameLine, $"'@name {name}' is not followed by a request");
			else if (hasDirective)
				Error(start, "per-request directives are not followed by a request");
			return;
		}

		ParseRequest(k, end, name ?? separatorName, new HttpFileDirectives(noRedirect, noCookieJar, timeout));
	}

	private int SkipScript(int k, int end)
	{
		for (var j = k; j < end; j++)
		{
			if (_lines[j].Contains("%}", StringComparison.Ordinal))
				return j;
		}
		return end - 1;
	}

	private static bool TryParseTimeout(string argument, out TimeSpan timeout)
	{
		timeout = default;
		var parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length is 0 or > 2)
			return false;
		var number = parts[0];
		var unit = parts.Length == 2 ? parts[1] : "s";
		if (parts.Length == 1)
		{
			foreach (var u in new[] { "ms", "s", "m" })
			{
				if (number.Length > u.Length && number.EndsWith(u, StringComparison.Ordinal) && char.IsDigit(number[^(u.Length + 1)]))
				{
					unit = u;
					number = number[..^u.Length];
					break;
				}
			}
		}
		if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
			return false;
		switch (unit)
		{
			case "ms":
				timeout = TimeSpan.FromMilliseconds(amount);
				return true;
			case "s":
				timeout = TimeSpan.FromSeconds(amount);
				return true;
			case "m":
				timeout = TimeSpan.FromMinutes(amount);
				return true;
			default:
				return false;
		}
	}

	private void ParseVariable(int k, string t)
	{
		var eq = t.IndexOf('=', StringComparison.Ordinal);
		var name = eq < 0 ? "" : t[1..eq].Trim();
		if (eq < 0 || name.Length == 0 || name.Any(char.IsWhiteSpace) || name.StartsWith('$') || name.Contains('{', StringComparison.Ordinal) || name.Contains('}', StringComparison.Ordinal))
		{
			Error(k, $"expected a variable definition '@name = value', got '{t}'");
			return;
		}
		var value = t[(eq + 1)..].Trim();
		_variables.Add(new HttpFileVariable(name, Template.Parse(value, k + 1, _path, _errors), k + 1));
	}

	private static readonly string[] KnownMethods =
		["GET", "HEAD", "POST", "PUT", "DELETE", "CONNECT", "OPTIONS", "TRACE", "PATCH", "PROPFIND", "PROPPATCH", "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK", "QUERY"];

	private void ParseRequest(int k, int end, string? name, HttpFileDirectives directives)
	{
		var requestLine = _lines[k].Trim();
		if (requestLine.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) || requestLine.Equals("curl", StringComparison.OrdinalIgnoreCase))
		{
			Error(k, "cURL commands are not supported; write the request as 'METHOD URL' followed by its headers");
			return;
		}

		var tokens = requestLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		var method = "GET";
		var first = 0;
		if (tokens.Length >= 2 && (KnownMethods.Contains(tokens[0], StringComparer.OrdinalIgnoreCase) || IsUpperWord(tokens[0])))
		{
			method = tokens[0].ToUpperInvariant();
			first = 1;
		}
		if (method is "WEBSOCKET" or "GRPC" or "GRAPHQL")
		{
			Error(k, $"{method} requests are not supported; only HTTP requests are");
			return;
		}
		string? version = null;
		var last = tokens.Length;
		if (last - first >= 2 && tokens[^1].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
		{
			version = tokens[^1].ToUpperInvariant();
			last--;
			if (version is not ("HTTP/1.0" or "HTTP/1.1" or "HTTP/2" or "HTTP/2.0" or "HTTP/3" or "HTTP/3.0"))
				Error(k, $"unsupported HTTP version '{tokens[^1]}'; use HTTP/1.1, HTTP/2 or HTTP/3");
		}
		var url = string.Join(' ', tokens[first..last]);
		var urlParts = new List<Template> { Template.Parse(url, k + 1, _path, _errors) };

		var i = k + 1;
		// Query continuation lines (REST Client: '?a=1' and '&b=2'), and JetBrains' indented URL continuation lines.
		for (; i < end; i++)
		{
			var raw = _lines[i];
			var t = raw.Trim();
			if (t.Length == 0)
				break;
			var indented = char.IsWhiteSpace(raw[0]);
			if (!(t.StartsWith('?') || t.StartsWith('&') || indented) || IsComment(t))
				break;
			urlParts.Add(Template.Parse(t, i + 1, _path, _errors));
		}

		var headers = new List<HttpFileHeader>();
		var stoppedAtHandler = false;
		for (; i < end; i++)
		{
			var t = _lines[i].Trim();
			if (t.Length == 0)
				break;
			if (IsComment(t))
				continue;
			if (t.StartsWith('>'))
			{
				i = RefuseResponseHandler(i, end);
				stoppedAtHandler = true;
				continue;
			}
			var colon = t.IndexOf(':', StringComparison.Ordinal);
			var headerName = colon > 0 ? t[..colon].Trim() : "";
			if (colon <= 0 || headerName.Any(char.IsWhiteSpace))
			{
				Error(i, $"expected a header 'Name: value' (or a blank line before the body), got '{t}'");
				continue;
			}
			var value = t[(colon + 1)..].Trim();
			if (headerName.Equals("X-REQUEST-TYPE", StringComparison.OrdinalIgnoreCase) && value.Equals("GraphQL", StringComparison.OrdinalIgnoreCase))
				Error(i, "GraphQL requests (X-REQUEST-TYPE: GraphQL) are not supported; send the query as a JSON body with Content-Type: application/json");
			headers.Add(new HttpFileHeader(headerName, Template.Parse(value, i + 1, _path, _errors), i + 1));
		}

		HttpFileBody? body = null;
		if (!stoppedAtHandler)
			body = ParseBody(i + 1, end, headers);

		_requests.Add(new HttpFileRequest(_requests.Count, name, k + 1, requestLine, method, Template.Concat(urlParts), version, headers, body, directives));
	}

	private static bool IsUpperWord(string token) => token.Length >= 3 && token.All(c => c is >= 'A' and <= 'Z');

	/// <summary>Refuses a JetBrains response handler or response redirect at line <paramref name="i"/>; returns the last line it covers.</summary>
	private int RefuseResponseHandler(int i, int end)
	{
		var t = _lines[i].Trim();
		if (t.StartsWith(">>", StringComparison.Ordinal))
		{
			Error(i, $"response redirection ('{(t.StartsWith(">>!", StringComparison.Ordinal) ? ">>!" : ">>")} file') is not supported; the run result holds the response");
			return i;
		}
		if (t.StartsWith("> {%", StringComparison.Ordinal))
		{
			Error(i, "JetBrains response handler scripts ('> {% ... %}') are not supported: they run JavaScript");
			return SkipScript(i, end);
		}
		Error(i, "JetBrains response handler files ('> handler.js') are not supported: they run JavaScript");
		return i;
	}

	private HttpFileBody? ParseBody(int from, int end, List<HttpFileHeader> headers)
	{
		var lines = new List<(string Text, int Index)>();
		for (var i = from; i < end; i++)
		{
			var t = _lines[i].Trim();
			if (t.StartsWith('>') || t.StartsWith("< {%", StringComparison.Ordinal))
			{
				i = t.StartsWith('>') ? RefuseResponseHandler(i, end) : RefusePreRequest(i, end);
				continue;
			}
			lines.Add((_lines[i], i));
		}
		while (lines.Count > 0 && lines[0].Text.Trim().Length == 0)
			lines.RemoveAt(0);
		while (lines.Count > 0 && lines[^1].Text.Trim().Length == 0)
			lines.RemoveAt(lines.Count - 1);
		if (lines.Count == 0)
			return null;

		var contentType = headers.LastOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
		var multipart = contentType.TrimStart().StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);
		var bodyLine = lines[0].Index + 1;

		if (!multipart)
		{
			if (TryFileLine(lines[0].Text, lines[0].Index, out var file))
			{
				if (lines.Count > 1)
					Error(lines[1].Index, "a file body ('< ./path') must be the only line of the body; to mix text and files use a multipart/* Content-Type");
				return new HttpFileBody(file.Kind, [file], bodyLine);
			}
			var text = string.Join('\n', lines.Select(l => l.Text));
			var part = new HttpFileBodyPart(HttpFileBodyKind.Text, Template.Parse(text, bodyLine, _path, _errors), null, null, null, bodyLine);
			return new HttpFileBody(HttpFileBodyKind.Text, [part], bodyLine);
		}

		// multipart: lines end in CRLF as RFC 2046 requires; '< ./path' lines are replaced by the file.
		var parts = new List<HttpFileBodyPart>();
		var buffer = new StringBuilder();
		var bufferLine = bodyLine;
		void Flush()
		{
			if (buffer.Length > 0)
				parts.Add(new HttpFileBodyPart(HttpFileBodyKind.Text, Template.Parse(buffer.ToString(), bufferLine, _path, _errors), null, null, null, bufferLine));
			buffer.Clear();
		}
		for (var n = 0; n < lines.Count; n++)
		{
			var (text, index) = lines[n];
			if (n > 0)
			{
				if (buffer.Length == 0)
					bufferLine = lines[n - 1].Index + 1;
				buffer.Append("\r\n");
			}
			if (TryFileLine(text, index, out var file))
			{
				Flush();
				parts.Add(file);
				bufferLine = index + 1;
				continue;
			}
			if (buffer.Length == 0)
				bufferLine = index + 1;
			buffer.Append(text);
		}
		Flush();
		if (parts.Count == 1 && parts[0].Kind == HttpFileBodyKind.Text)
			return new HttpFileBody(HttpFileBodyKind.Text, parts, bodyLine);
		return new HttpFileBody(HttpFileBodyKind.Mixed, parts, bodyLine);
	}

	private int RefusePreRequest(int i, int end)
	{
		Error(i, "JetBrains pre-request scripts ('< {% ... %}') are not supported: they run JavaScript");
		return SkipScript(i, end);
	}

	/// <summary><c>&lt; ./path</c>, <c>&lt;@ ./path</c> or <c>&lt;@latin1 ./path</c>.</summary>
	private bool TryFileLine(string raw, int index, out HttpFileBodyPart part)
	{
		part = null!;
		var t = raw.Trim();
		if (!t.StartsWith('<') || t.Length < 2)
			return false;
		HttpFileBodyKind kind;
		string? encoding = null;
		string filePath;
		if (char.IsWhiteSpace(t[1]))
		{
			kind = HttpFileBodyKind.File;
			filePath = t[1..].Trim();
		}
		else if (t[1] == '@')
		{
			kind = HttpFileBodyKind.FileWithVariables;
			var rest = t[2..];
			var space = rest.IndexOfAny([' ', '\t']);
			if (space < 0)
				return false;
			var name = rest[..space];
			filePath = rest[space..].Trim();
			if (name.Length > 0)
			{
				encoding = name;
				if (!TryGetEncoding(name, out _))
					Error(index, $"unknown encoding '{name}' in '<@{name}'; use a name such as utf-8, utf-16 or latin1");
			}
		}
		else
		{
			return false;
		}

		if (filePath.Length == 0)
		{
			Error(index, "a file body line needs a path, e.g. '< ./body.json'");
			filePath = ".";
		}
		var full = Path.GetFullPath(Path.Combine(_directory, filePath));
		part = new HttpFileBodyPart(kind, null, filePath, full, encoding, index + 1);
		return true;
	}

	internal static bool TryGetEncoding(string name, out Encoding encoding)
	{
		try
		{
			encoding = Encoding.GetEncoding(name);
			return true;
		}
		catch (ArgumentException)
		{
			encoding = Encoding.UTF8;
			return false;
		}
	}
}
