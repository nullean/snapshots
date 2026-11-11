using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Nullean.HttpFileParser;

/// <summary>
/// Runs the requests of a <see cref="HttpFile"/> in order against an <see cref="HttpClient"/>, resolving variables,
/// following redirects itself and decoding compressed bodies. The run stops at the first request that cannot be
/// resolved, sent or read; an error status is a response, not a failure.
/// </summary>
public static class HttpFileRunner
{
	/// <summary>
	/// Runs every request of <paramref name="file"/>.
	/// </summary>
	/// <param name="file">The parsed file.</param>
	/// <param name="client">
	/// The client to send with; <see langword="null"/> creates one that neither follows redirects nor keeps cookies itself
	/// and decompresses gzip, deflate and brotli. A URL starting with <c>/</c> resolves against
	/// <see cref="HttpClient.BaseAddress"/>. The runner follows redirects itself (honouring <c>@no-redirect</c>), so a
	/// caller's client should not: create it with <c>AllowAutoRedirect = false</c>. The runner's cookie jar is used only
	/// with its own client; a caller's client keeps cookies as its handler does.
	/// </param>
	/// <param name="options">Environment, variables, secrets, clock and randomness.</param>
	/// <param name="cancellationToken">Cancels the run; cancellation throws instead of becoming a failure.</param>
	public static async Task<HttpFileRunResult> RunAsync(
		HttpFile file,
		HttpClient? client = null,
		HttpFileRunOptions? options = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(file);
		options ??= new HttpFileRunOptions();
		var environment = HttpEnvironment.Load(file.Path, options.Environment);
		var resolver = new Resolver(file, environment, options);
		var ownsClient = client is null;
		CookieContainer? cookies = null;
		if (client is null)
		{
			cookies = new CookieContainer();
			client = new HttpClient(new SocketsHttpHandler
			{
				AllowAutoRedirect = false,
				UseCookies = false,
				AutomaticDecompression = DecompressionMethods.All,
			});
		}

		var exchanges = new List<HttpFileExchange>();
		HttpFileRunFailure? failure = null;
		try
		{
			foreach (var request in file.Requests)
			{
				if (options.ReadOnly && RefusedAsWrite(request) is { } refusal)
				{
					failure = Fail(file, request, request.Line, refusal, null, resolver);
					break;
				}
				try
				{
					var exchange = await SendAsync(request, client, cookies, resolver, options, cancellationToken).ConfigureAwait(false);
					exchanges.Add(exchange);
					resolver.Completed(exchange);
					failure = Unmet(file, request, exchange, resolver);
				}
				catch (ResolveException e)
				{
					failure = Fail(file, request, e.Line, e.Message, null, resolver);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (OperationCanceledException e)
				{
					var limit = request.Directives.Timeout is { } t
						? $"the @timeout of {t.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}s"
						: "HttpClient.Timeout";
					failure = Fail(file, request, request.Line, $"timed out ({limit})", e, resolver);
				}
				catch (HttpRequestException e)
				{
					failure = Fail(file, request, request.Line, Describe(e), e, resolver);
				}
				catch (Exception e) when (e is IOException or InvalidOperationException or UriFormatException or UnauthorizedAccessException or NotSupportedException or FormatException)
				{
					failure = Fail(file, request, request.Line, Describe(e), e, resolver);
				}
				if (failure is not null)
					break;
			}
		}
		finally
		{
			if (ownsClient)
				client.Dispose();
		}
		return new HttpFileRunResult(file, exchanges, failure, [.. resolver.Sensitive]);
	}

	/// <summary>Why a read-only run does not send <paramref name="request"/>; <see langword="null"/> when it may be sent.</summary>
	private static string? RefusedAsWrite(HttpFileRequest request) =>
		request.Directives.ReadOnly || request.Method.ToUpperInvariant() is "GET" or "HEAD" or "OPTIONS"
			? null
			: $"not sent: a read-only run sends only GET, HEAD and OPTIONS requests, and this is {request.Method}. Put '# @readonly' before the request if it only reads";

	/// <summary>The first <c>@expect</c> of <paramref name="request"/> that the response does not meet, as a failure.</summary>
	private static HttpFileRunFailure? Unmet(HttpFile file, HttpFileRequest request, HttpFileExchange exchange, Resolver resolver)
	{
		foreach (var expectation in request.Directives.Expectations)
		{
			if (expectation.Check(exchange.Response) is { } problem)
				return Fail(file, request, expectation.Line, problem, null, resolver);
		}
		return null;
	}

	private static HttpFileRunFailure Fail(HttpFile file, HttpFileRequest request, int line, string message, Exception? exception, Resolver resolver) =>
		new(file.Path, request, line, HttpFileRunResult.Redact(message, HttpFileRunResult.SortForRedaction(resolver.Sensitive)), exception);

	private static string Describe(Exception e)
	{
		var message = e.Message;
		for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
		{
			if (!message.Contains(inner.Message, StringComparison.Ordinal))
				message += " " + inner.Message;
		}
		return message;
	}

	private sealed class Outgoing
	{
		public required string Method { get; init; }
		public required Uri Uri { get; init; }
		public required Version Version { get; init; }
		public required List<HttpHeaderField> Headers { get; init; }
		public required byte[]? Body { get; init; }
	}

	private static async Task<HttpFileExchange> SendAsync(
		HttpFileRequest request,
		HttpClient client,
		CookieContainer? cookies,
		Resolver resolver,
		HttpFileRunOptions options,
		CancellationToken ct
	)
	{
		var outgoing = await ResolveAsync(request, client, resolver, ct).ConfigureAwait(false);
		var first = outgoing;

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		if (request.Directives.Timeout is { } limit)
			timeout.CancelAfter(limit);

		var redirects = new List<HttpRedirect>();
		var useCookies = cookies is not null && !request.Directives.NoCookieJar;
		string? firstJar = null;
		while (true)
		{
			var jar = useCookies ? cookies!.GetCookieHeader(outgoing.Uri) : "";
			if (redirects.Count == 0 && jar.Length > 0)
				firstJar = jar;

			using var message = Build(outgoing, jar);
			using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
			var raw = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);

			if (useCookies && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
			{
				foreach (var setCookie in setCookies)
				{
					try
					{
						cookies!.SetCookies(outgoing.Uri, setCookie);
					}
					catch (CookieException)
					{
						// A malformed cookie is the server's problem; the response still records it.
					}
				}
			}

			var status = (int)response.StatusCode;
			var location = response.Headers.Location;
			if (!request.Directives.NoRedirect && status is 301 or 302 or 303 or 307 or 308 && location is not null)
			{
				if (redirects.Count >= options.MaxRedirects)
					throw new ResolveException(request.Line, $"stopped after {options.MaxRedirects} redirects (last to {location})");
				var target = location.IsAbsoluteUri ? location : new Uri(outgoing.Uri, location);
				redirects.Add(new HttpRedirect(status, target));
				outgoing = Redirect(outgoing, status, target);
				continue;
			}

			var (body, encoding) = Decode(raw, response.Content.Headers.ContentEncoding);
			var contentType = response.Content.Headers.ContentType;
			var received = new HttpFileResponse(
				status,
				response.ReasonPhrase,
				response.Version,
				Fields(response.Headers),
				Fields(response.Content.Headers),
				body,
				contentType?.MediaType,
				contentType?.CharSet,
				encoding
			);
			var sentHeaders = firstJar is null ? first.Headers : [.. first.Headers, new HttpHeaderField("Cookie", firstJar)];
			var sent = new HttpFileSentRequest(first.Method, first.Uri, first.Version, sentHeaders, first.Body);
			return new HttpFileExchange(request, sent, received, redirects);
		}
	}

	/// <summary>
	/// The next request of a redirect: 303, and 301/302 after a POST, become a GET without a body, as browsers and HttpClient
	/// do; 307 and 308 repeat the request. Credentials are not sent to another origin.
	/// </summary>
	private static Outgoing Redirect(Outgoing previous, int status, Uri target)
	{
		var toGet = (status == 303 && previous.Method != "HEAD") || (status is 301 or 302 && previous.Method == "POST");
		var sameOrigin = Uri.Compare(previous.Uri, target, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
		var headers = previous
			.Headers
			.Where(
				h => sameOrigin ||
					!(h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
			)
			.Where(h => !toGet || !IsContentHeader(h.Name))
			.ToList();
		return new Outgoing
		{
			Method = toGet ? "GET" : previous.Method,
			Uri = target,
			Version = previous.Version,
			Headers = headers,
			Body = toGet ? null : previous.Body,
		};
	}

	private static bool IsContentHeader(string name) =>
		name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || name.Equals("Expires", StringComparison.OrdinalIgnoreCase) || name.Equals(
			"Last-Modified",
			StringComparison.OrdinalIgnoreCase
		) || name.Equals("Allow", StringComparison.OrdinalIgnoreCase);

	private static HttpRequestMessage Build(Outgoing outgoing, string jar)
	{
		var message = new HttpRequestMessage(new HttpMethod(outgoing.Method), outgoing.Uri)
		{
			Version = outgoing.Version,
			VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
		};
		var contentHeaders = new List<HttpHeaderField>();
		foreach (var header in outgoing.Headers)
		{
			if (IsContentHeader(header.Name) || !message.Headers.TryAddWithoutValidation(header.Name, header.Value))
				contentHeaders.Add(header);
		}
		if (jar.Length > 0)
		{
			var written = message.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
			message.Headers.Remove("Cookie");
			message.Headers.TryAddWithoutValidation("Cookie", written is null ? jar : written + "; " + jar);
		}
		if (outgoing.Body is not null || contentHeaders.Count > 0)
		{
			message.Content = new ByteArrayContent(outgoing.Body ?? []);
			foreach (var header in contentHeaders)
			{
				if (!message.Content.Headers.TryAddWithoutValidation(header.Name, header.Value))
					throw new InvalidOperationException($"header '{header.Name}' cannot be sent");
			}
		}
		return message;
	}

	private static async Task<Outgoing> ResolveAsync(HttpFileRequest request, HttpClient client, Resolver resolver, CancellationToken ct)
	{
		var scope = request.Line;
		var url = resolver.Render(request.Url, scope).Trim();
		var uri = ResolveUri(url, client, request.Line);
		var headers = request.Headers.Select(h => new HttpHeaderField(h.Name, resolver.Render(h.Template, scope))).ToList();
		var body = await BodyAsync(request, headers, resolver, ct).ConfigureAwait(false);
		var version = request.Version switch
		{
			"HTTP/1.0" => HttpVersion.Version10,
			"HTTP/2" or "HTTP/2.0" => HttpVersion.Version20,
			"HTTP/3" or "HTTP/3.0" => HttpVersion.Version30,
			"HTTP/1.1" => HttpVersion.Version11,
			_ => client.DefaultRequestVersion,
		};
		return new Outgoing { Method = request.Method, Uri = uri, Version = version, Headers = headers, Body = body };
	}

	/// <summary>Absolute URLs as they are; <c>/path</c> against <see cref="HttpClient.BaseAddress"/>; <c>host/path</c> gets <c>http://</c>.</summary>
	internal static Uri ResolveUri(string url, HttpClient client, int line)
	{
		if (url.StartsWith('/'))
		{
			if (client.BaseAddress is null)
				throw new ResolveException(line, $"'{url}' is relative, and the HttpClient has no BaseAddress to resolve it against");
			return new Uri(client.BaseAddress, url);
		}
		if (!HasScheme(url))
			url = "http://" + url;
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			throw new ResolveException(line, $"'{url}' is not a valid URL");
		return uri;
	}

	private static bool HasScheme(string url)
	{
		var i = url.IndexOf("://", StringComparison.Ordinal);
		if (i <= 0)
			return false;
		for (var j = 0; j < i; j++)
		{
			var c = url[j];
			if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') || (j == 0 && !char.IsAsciiLetter(c)))
				return false;
		}
		return true;
	}

	private static async Task<byte[]?> BodyAsync(HttpFileRequest request, List<HttpHeaderField> headers, Resolver resolver, CancellationToken ct)
	{
		if (request.Body is not { } body)
			return null;
		var contentType = headers.LastOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? "";
		var textEncoding = Encoding.UTF8;
		if (
			MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed.CharSet is { } charSet && HttpFileParser.TryGetEncoding(
				charSet.Trim('"'),
				out var named
			)
		)
			textEncoding = named;
		var formUrlEncoded = parsed?.MediaType?.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true;

		using var buffer = new MemoryStream();
		foreach (var part in body.Parts)
		{
			switch (part.Kind)
			{
				case HttpFileBodyKind.Text:
					{
						var text = resolver.Render(part.Template!, request.Line);
						if (formUrlEncoded)
							text = string.Concat(text.Split('\n').Select(l => l.Trim()));
						buffer.Write(textEncoding.GetBytes(text));
						break;
					}
				case HttpFileBodyKind.File:
					buffer.Write(await ReadFileAsync(part, ct).ConfigureAwait(false));
					break;
				case HttpFileBodyKind.FileWithVariables:
					{
						var encoding = part.Encoding is { } name && HttpFileParser.TryGetEncoding(name, out var e) ? e : Encoding.UTF8;
						var text = encoding.GetString(await ReadFileAsync(part, ct).ConfigureAwait(false));
						var errors = new List<HttpFileError>();
						var template = Template.Parse(text, part.Line, part.FilePath!, errors, countLines: false);
						if (errors.Count > 0)
							throw new ResolveException(part.Line, $"in '{part.FilePath}': {errors[0].Message}");
						buffer.Write(encoding.GetBytes(resolver.Render(template, request.Line)));
						break;
					}
				default:
					break;
			}
		}
		return buffer.ToArray();
	}

	private static async Task<byte[]> ReadFileAsync(HttpFileBodyPart part, CancellationToken ct)
	{
		if (!File.Exists(part.FullPath))
			throw new ResolveException(part.Line, $"body file '{part.FilePath}' does not exist ({part.FullPath})");
		return await File.ReadAllBytesAsync(part.FullPath, ct).ConfigureAwait(false);
	}

	private static List<HttpHeaderField> Fields(HttpHeaders headers) => [.. headers.SelectMany(h => h.Value.Select(v => new HttpHeaderField(h.Key, v)))];

	/// <summary>Undoes <c>Content-Encoding</c> a client's handler left in place, last applied first.</summary>
	internal static (byte[] Body, string? Encoding) Decode(byte[] body, ICollection<string> encodings)
	{
		var applied = encodings.Where(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)).ToList();
		if (applied.Count == 0 || applied.Any(e => !IsKnown(e)))
			return (body, null);
		for (var i = applied.Count - 1; i >= 0; i--)
			body = DecodeOne(body, applied[i].ToLowerInvariant());
		return (body, string.Join(", ", applied));
	}

	private static bool IsKnown(string encoding) => encoding.ToLowerInvariant() is "gzip" or "x-gzip" or "deflate" or "br";

	private static byte[] DecodeOne(byte[] body, string encoding)
	{
		if (encoding == "deflate")
		{
			// HTTP's deflate is zlib-wrapped, but some servers send raw deflate.
			try
			{
				return Inflate(new ZLibStream(new MemoryStream(body), CompressionMode.Decompress));
			}
			catch (InvalidDataException)
			{
				return Inflate(new DeflateStream(new MemoryStream(body), CompressionMode.Decompress));
			}
		}
		return Inflate(
			encoding == "br"
				? new BrotliStream(new MemoryStream(body), CompressionMode.Decompress)
				: new GZipStream(new MemoryStream(body), CompressionMode.Decompress)
		);
	}

	private static byte[] Inflate(Stream stream)
	{
		using (stream)
		{
			using var output = new MemoryStream();
			stream.CopyTo(output);
			return output.ToArray();
		}
	}
}
