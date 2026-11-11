using System.Text;

namespace Nullean.HttpFileParser;

/// <summary>A header name and value, as sent or received.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">The value; a header with several values appears once per value.</param>
public readonly record struct HttpHeaderField(string Name, string Value);

/// <summary>A redirect the runner followed.</summary>
/// <param name="StatusCode">The 3xx status.</param>
/// <param name="Location">The absolute URI it pointed to.</param>
public readonly record struct HttpRedirect(int StatusCode, Uri Location);

/// <summary>A request as the runner sent it, every reference resolved.</summary>
public sealed class HttpFileSentRequest
{
	internal HttpFileSentRequest(string method, Uri uri, Version version, IReadOnlyList<HttpHeaderField> headers, byte[]? body)
	{
		Method = method;
		Uri = uri;
		Version = version;
		Headers = headers;
		Body = body;
	}

	/// <summary>The method.</summary>
	public string Method { get; }

	/// <summary>The absolute URI.</summary>
	public Uri Uri { get; }

	/// <summary>The HTTP version requested.</summary>
	public Version Version { get; }

	/// <summary>The headers written in the file, resolved, content headers included, plus a <c>Cookie</c> header from the runner's cookie jar.</summary>
	public IReadOnlyList<HttpHeaderField> Headers { get; }

	/// <summary>The body bytes; <see langword="null"/> when the request has no body.</summary>
	public byte[]? Body { get; }

	/// <summary>The body as UTF-8 text; <see langword="null"/> when there is no body.</summary>
	public string? BodyText => Body is null ? null : Encoding.UTF8.GetString(Body);

	/// <inheritdoc />
	public override string ToString() => $"{Method} {Uri}";
}

/// <summary>A response as received, its body read and decoded.</summary>
public sealed class HttpFileResponse
{
	internal HttpFileResponse(
		int statusCode,
		string? reasonPhrase,
		Version version,
		IReadOnlyList<HttpHeaderField> headers,
		IReadOnlyList<HttpHeaderField> contentHeaders,
		byte[] body,
		string? mediaType,
		string? charSet,
		string? contentEncoding
	)
	{
		StatusCode = statusCode;
		ReasonPhrase = reasonPhrase;
		Version = version;
		Headers = headers;
		ContentHeaders = contentHeaders;
		Body = body;
		MediaType = mediaType;
		CharSet = charSet;
		ContentEncoding = contentEncoding;
	}

	/// <summary>The status code.</summary>
	public int StatusCode { get; }

	/// <summary>The reason phrase.</summary>
	public string? ReasonPhrase { get; }

	/// <summary>The HTTP version of the response.</summary>
	public Version Version { get; }

	/// <summary>The response headers, in the order received.</summary>
	public IReadOnlyList<HttpHeaderField> Headers { get; }

	/// <summary>The content headers (<c>Content-Type</c>, <c>Content-Length</c>, ...), as received.</summary>
	public IReadOnlyList<HttpHeaderField> ContentHeaders { get; }

	/// <summary>The body bytes, decompressed.</summary>
	public byte[] Body { get; }

	/// <summary>The media type of <c>Content-Type</c>, without parameters.</summary>
	public string? MediaType { get; }

	/// <summary>The charset of <c>Content-Type</c>.</summary>
	public string? CharSet { get; }

	/// <summary>
	/// The <c>Content-Encoding</c> the runner decoded (<c>gzip</c>, <c>deflate</c>, <c>br</c>); <see langword="null"/> when the
	/// body was not compressed or the client's handler had already decompressed it.
	/// </summary>
	public string? ContentEncoding { get; }

	/// <summary>The body as text, in <see cref="CharSet"/> when it names a known encoding, else UTF-8.</summary>
	public string BodyText => (CharSet is not null && HttpFileParser.TryGetEncoding(CharSet.Trim('"'), out var e) ? e : Encoding.UTF8).GetString(Body);

	/// <summary>Every value of a header, response and content headers both, by case-insensitive name.</summary>
	public IEnumerable<string> GetHeaderValues(string name) =>
		Headers.Concat(ContentHeaders).Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

	/// <inheritdoc />
	public override string ToString() => $"{StatusCode} {ReasonPhrase}";
}

/// <summary>One request of the file and what came back.</summary>
public sealed class HttpFileExchange
{
	internal HttpFileExchange(HttpFileRequest request, HttpFileSentRequest sent, HttpFileResponse response, IReadOnlyList<HttpRedirect> redirects)
	{
		Request = request;
		Sent = sent;
		Response = response;
		Redirects = redirects;
	}

	/// <summary>The request as written in the file.</summary>
	public HttpFileRequest Request { get; }

	/// <summary>The request as sent, references resolved. With redirects, the first request.</summary>
	public HttpFileSentRequest Sent { get; }

	/// <summary>The final response, after any redirects.</summary>
	public HttpFileResponse Response { get; }

	/// <summary>The redirects followed to reach <see cref="Response"/>, in order.</summary>
	public IReadOnlyList<HttpRedirect> Redirects { get; }

	/// <inheritdoc />
	public override string ToString() => $"{Request.RequestLine} -> {Response}";
}

/// <summary>Why a run stopped: a request could not be resolved, sent or read.</summary>
public sealed class HttpFileRunFailure
{
	internal HttpFileRunFailure(string path, HttpFileRequest request, int line, string message, Exception? exception)
	{
		Path = path;
		Request = request;
		Line = line;
		Message = message;
		Exception = exception;
	}

	/// <summary>The <c>.http</c> file.</summary>
	public string Path { get; }

	/// <summary>The request that failed.</summary>
	public HttpFileRequest Request { get; }

	/// <summary>The 1-based line of the problem: the reference that could not be resolved, else the request line.</summary>
	public int Line { get; }

	/// <summary>What went wrong, redacted.</summary>
	public string Message { get; }

	/// <summary>The exception behind the failure, when there was one. Its own message is not redacted.</summary>
	public Exception? Exception { get; }

	/// <summary>The failure as <c>path:line: message</c>.</summary>
	public override string ToString() => $"{Path}:{Line}: {Message}";
}

/// <summary>A value that came from a secret, the process environment, a <c>.env</c> file or a private environment file, and the reference that stands for it.</summary>
public sealed class HttpSensitiveValue
{
	internal HttpSensitiveValue(string value, string reference)
	{
		Value = value;
		Reference = reference;
	}

	/// <summary>The value. Never printed by <see cref="ToString"/>.</summary>
	public string Value { get; }

	/// <summary>What replaces the value in redacted text: <c>{{$processEnv API_KEY}}</c>, <c>{{ApiKey}}</c>, <c>{{$secret Name}}</c>.</summary>
	public string Reference { get; }

	/// <summary>The reference; never the value.</summary>
	public override string ToString() => Reference;
}

/// <summary>What a run of a <c>.http</c> file did: the exchanges in order, and the failure that stopped it if any.</summary>
public sealed class HttpFileRunResult
{
	private readonly List<HttpSensitiveValue> _byLength;

	internal HttpFileRunResult(
		HttpFile file,
		IReadOnlyList<HttpFileExchange> exchanges,
		HttpFileRunFailure? failure,
		IReadOnlyList<HttpSensitiveValue> sensitive
	)
	{
		File = file;
		Exchanges = exchanges;
		Failure = failure;
		SensitiveValues = sensitive;
		_byLength = SortForRedaction(sensitive);
	}

	/// <summary>The file that ran.</summary>
	public HttpFile File { get; }

	/// <summary>The exchanges, in file order, up to the failure. They hold what went over the wire: use <see cref="Redact(HttpFileExchange)"/> before showing them.</summary>
	public IReadOnlyList<HttpFileExchange> Exchanges { get; }

	/// <summary>Why the run stopped early; <see langword="null"/> when every request ran.</summary>
	public HttpFileRunFailure? Failure { get; }

	/// <summary>Whether every request ran. An error status (404, 500) is still a response, not a failure.</summary>
	public bool Succeeded => Failure is null;

	/// <summary>Every sensitive value the run used, with the reference that stands for it.</summary>
	public IReadOnlyList<HttpSensitiveValue> SensitiveValues { get; }

	internal static List<HttpSensitiveValue> SortForRedaction(IEnumerable<HttpSensitiveValue> values) =>
		[.. values.OrderByDescending(v => v.Value.Length).ThenBy(v => v.Value, StringComparer.Ordinal)];

	/// <summary>Replaces every sensitive value in <paramref name="text"/> with its reference, longest value first.</summary>
	public string Redact(string text) => Redact(text, _byLength);

	internal static string Redact(string text, IReadOnlyList<HttpSensitiveValue> byLength)
	{
		ArgumentNullException.ThrowIfNull(text);
		foreach (var value in byLength)
			text = text.Replace(value.Value, value.Reference, StringComparison.Ordinal);
		return text;
	}

	/// <summary>
	/// A copy of <paramref name="exchange"/> with every sensitive value replaced by its reference: in the URI, the request
	/// and response headers, and the request and response bodies when they are UTF-8 text.
	/// </summary>
	public HttpFileExchange Redact(HttpFileExchange exchange)
	{
		ArgumentNullException.ThrowIfNull(exchange);
		var sent = exchange.Sent;
		var response = exchange.Response;
		var redactedSent = new HttpFileSentRequest(sent.Method, RedactUri(sent.Uri), sent.Version, RedactHeaders(sent.Headers), RedactBody(sent.Body));
		var redactedResponse = new HttpFileResponse(
			response.StatusCode,
			response.ReasonPhrase is null ? null : Redact(response.ReasonPhrase),
			response.Version,
			RedactHeaders(response.Headers),
			RedactHeaders(response.ContentHeaders),
			RedactBody(response.Body)!,
			response.MediaType,
			response.CharSet,
			response.ContentEncoding
		);
		var redirects = exchange.Redirects.Select(r => r with { Location = RedactUri(r.Location) }).ToList();
		return new HttpFileExchange(exchange.Request, redactedSent, redactedResponse, redirects);
	}

	private Uri RedactUri(Uri uri)
	{
		var text = Redact(uri.OriginalString);
		return text == uri.OriginalString ? uri : new Uri(text, UriKind.RelativeOrAbsolute);
	}

	private List<HttpHeaderField> RedactHeaders(IReadOnlyList<HttpHeaderField> headers) => [.. headers.Select(h => h with { Value = Redact(h.Value) })];

	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	private byte[]? RedactBody(byte[]? body)
	{
		if (body is null || _byLength.Count == 0)
			return body;
		string text;
		try
		{
			text = StrictUtf8.GetString(body);
		}
		catch (DecoderFallbackException)
		{
			return body;
		}
		var redacted = Redact(text);
		return redacted == text ? body : Encoding.UTF8.GetBytes(redacted);
	}
}
