using System.Text;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Http;

/// <summary>What an HTTP snapshot writes beyond the status and body.</summary>
public sealed class HttpSnapshotOptions
{
	/// <summary>
	/// Headers to write on top of <c>content-type</c> and <c>location</c>, by case-insensitive name. <c>authorization</c>,
	/// <c>cookie</c>, <c>set-cookie</c> and <c>proxy-authorization</c> are never written, even when named here.
	/// </summary>
	public IReadOnlyCollection<string> Headers { get; init; } = [];

	/// <summary>
	/// Writes the body as its shape: every JSON value replaced by its kind, arrays collapsed, a text body as its media
	/// type. For a live service whose data drifts but whose properties should not.
	/// </summary>
	public bool Shape { get; init; }
}

/// <summary>
/// Snapshots of HTTP responses: the status, chosen headers and the decoded body, a JSON body as a tree.
/// </summary>
/// <remarks>
/// <code>
/// ## POST /api/login
/// status: 200 OK
/// content-type: application/json; charset=utf-8
///
/// {
///   "token": "abc"
/// }
/// </code>
/// </remarks>
public static class HttpSnapshot
{
	/// <summary>The <c>.responses</c> format.</summary>
	public static HttpSnapshotFormat Format { get; } = new();

	/// <summary>The <c>.responses</c> format comparing trees with <paramref name="diff"/> (ignored paths, strict numbers); <see cref="Format"/> when it is <see langword="null"/>.</summary>
	public static HttpSnapshotFormat FormatWith(TreeDiffOptions? diff) => diff is null ? Format : new HttpSnapshotFormat(diff);

	/// <summary>
	/// Renders one response: headed by its request's method, path and query (<c>## response</c> when it has none),
	/// then the status, the chosen headers and the body. A body still carrying <c>Content-Encoding</c> gzip, deflate or
	/// br is decoded.
	/// </summary>
	/// <param name="response">The response; its content is buffered, so it can still be read afterwards.</param>
	/// <param name="options">Which headers to write.</param>
	/// <param name="cancellationToken">Stops reading the body.</param>
	public static async Task<string> RenderAsync(
		HttpResponseMessage response,
		HttpSnapshotOptions? options = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(response);
		var headers = response
			.Headers
			.Concat(response.Content.Headers)
			.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
			.ToList();
		var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
		var mediaType = response.Content.Headers.ContentType?.MediaType;
		if (response.Content.Headers.ContentEncoding.Count > 0 && body.Length > 0)
		{
			// A handler without automatic decompression leaves the body encoded; one we cannot decode stays binary.
			if (HttpResponseText.Decompress(body, response.Content.Headers.ContentEncoding) is { } decoded)
				body = decoded;
			else
				mediaType = null;
		}

		var section = new HttpResponseSection(
			Heading(response.RequestMessage),
			(int)response.StatusCode,
			response.ReasonPhrase,
			headers,
			body,
			mediaType,
			mediaType is null ? null : response.Content.Headers.ContentType?.CharSet
		);
		var text = new StringBuilder();
		HttpResponseText.Append(text, section, HttpResponseText.Chosen(options?.Headers), options?.Shape ?? false);
		return text.ToString();
	}

	private static string Heading(HttpRequestMessage? request)
	{
		if (request is null)
			return "response";
		var target = request.RequestUri switch
		{
			null => "/",
			{ IsAbsoluteUri: true } uri => uri.PathAndQuery,
			var uri => uri.OriginalString,
		};
		return $"{request.Method.Method} {target}";
	}
}

/// <summary>
/// HTTP responses (<c>.responses</c>), compared section by section as trees: the request heading, the status, the
/// headers in any order and a JSON body by value, with the line diff after.
/// </summary>
public sealed class HttpSnapshotFormat(TreeDiffOptions? options = null) : TreeSnapshotFormat(options)
{
	/// <inheritdoc />
	public override string Name => "HTTP";

	/// <inheritdoc />
	public override string Extension => "responses";

	/// <summary>
	/// Parses responses text into a sequence of sections, <c>{request, status, headers, body | text | text-type | binary}</c>:
	/// <c>body</c> is a JSON body's tree, <c>text</c> any other text, <c>text-type</c> a text body's media type in a shape,
	/// and parts that are empty are left out.
	/// </summary>
	/// <exception cref="FormatException">The text is not in this format; the message starts with the line.</exception>
	public override Node Parse(string text) => HttpResponseText.Parse(text);

	/// <inheritdoc />
	public override SnapshotComparison Compare(string verified, string actual)
	{
		var comparison = base.Compare(verified, actual);
		return comparison.IsMatch ? comparison : comparison with { LineDiff = LineDiff.Hunks(verified, actual) };
	}
}
