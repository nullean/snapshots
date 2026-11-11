namespace Nullean.HttpFileParser;

/// <summary>
/// A parsed <c>.http</c> file: its requests in file order and its file variables. Parsing never sends anything and
/// never reads environment or body files; <see cref="Validate"/> checks what can be known before a run.
/// </summary>
public sealed class HttpFile
{
	internal HttpFile(string path, IReadOnlyList<HttpFileRequest> requests, IReadOnlyList<HttpFileVariable> variables)
	{
		Path = path;
		Requests = requests;
		Variables = variables;
	}

	/// <summary>What the header's <c>@</c> directives say; <see cref="HttpFileMetadata.None"/> without any.</summary>
	public HttpFileMetadata Metadata { get; internal init; } = HttpFileMetadata.None;

	/// <summary>The path the file was parsed or loaded with. Errors use it, and relative body files and environment files are found from its folder.</summary>
	public string Path { get; }

	/// <summary>The requests, in file order.</summary>
	public IReadOnlyList<HttpFileRequest> Requests { get; }

	/// <summary>The file variables (<c>@name = value</c>), in file order. A variable is visible to the requests and variables after its line.</summary>
	public IReadOnlyList<HttpFileVariable> Variables { get; }

	/// <summary>The folder relative paths resolve against: the folder of <see cref="Path"/>.</summary>
	internal string Directory => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? System.IO.Path.GetFullPath(".");

	/// <summary>Parses <paramref name="text"/>. Throws <see cref="HttpFileParseException"/> listing every problem when the file has any.</summary>
	/// <param name="text">The content of the file.</param>
	/// <param name="path">The path to report errors with and to resolve relative body and environment files from.</param>
	public static HttpFile Parse(string text, string path = "request.http")
	{
		ArgumentNullException.ThrowIfNull(text);
		ArgumentNullException.ThrowIfNull(path);
		return HttpFileParser.Parse(text, path);
	}

	/// <summary>Reads and parses the file at <paramref name="path"/>. Throws <see cref="HttpFileParseException"/> listing every problem when the file has any.</summary>
	public static HttpFile Load(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		return HttpFileParser.Parse(File.ReadAllText(path), path);
	}

	/// <summary>
	/// Lints the file without sending anything: variables used before their definition or never defined (in the file,
	/// <paramref name="environment"/> or <paramref name="variables"/>), references to requests that are not earlier in
	/// the file, and body files that do not exist. Dynamic values (<c>$processEnv</c>, <c>$dotenv</c>, secrets) are only
	/// known during a run and are not checked.
	/// </summary>
	/// <param name="environment">The environment a run would use; <see langword="null"/> for none.</param>
	/// <param name="variables">Variables a run would be given through <see cref="HttpFileRunOptions.Variables"/>.</param>
	/// <returns>Every problem found, in file order; empty when there are none.</returns>
	public IReadOnlyList<HttpFileError> Validate(HttpEnvironment? environment = null, IReadOnlyDictionary<string, string>? variables = null) =>
		HttpFileValidator.Validate(this, environment, variables);
}

/// <summary>A file variable, <c>@name = value</c>.</summary>
public sealed class HttpFileVariable
{
	internal HttpFileVariable(string name, Template value, int line)
	{
		Name = name;
		Template = value;
		Line = line;
	}

	/// <summary>The case-sensitive name.</summary>
	public string Name { get; }

	/// <summary>The value as written, with its <c>{{ }}</c> references unresolved.</summary>
	public string Value => Template.Text;

	/// <summary>The 1-based line of the definition.</summary>
	public int Line { get; }

	internal Template Template { get; }

	/// <inheritdoc />
	public override string ToString() => $"@{Name} = {Value}";
}

/// <summary>Per-request settings from comment lines before the request: <c>@no-redirect</c>, <c>@no-cookie-jar</c>, <c>@timeout</c>, <c>@readonly</c> and <c>@expect</c>.</summary>
public sealed class HttpFileDirectives
{
	internal static HttpFileDirectives None { get; } = new(false, false, null, false, []);

	internal HttpFileDirectives(bool noRedirect, bool noCookieJar, TimeSpan? timeout, bool readOnly, IReadOnlyList<HttpExpectation> expectations)
	{
		NoRedirect = noRedirect;
		NoCookieJar = noCookieJar;
		Timeout = timeout;
		ReadOnly = readOnly;
		Expectations = expectations;
	}

	/// <summary>
	/// <c>@readonly</c>: the request only reads, though its method is not GET, HEAD or OPTIONS (a JSON-RPC call over POST,
	/// say). A read-only run (<see cref="HttpFileRunOptions.ReadOnly"/>) sends such a request and refuses any other POST,
	/// PUT, PATCH or DELETE.
	/// </summary>
	public bool ReadOnly { get; }

	/// <summary>
	/// <c>@expect</c> lines, in file order: <c>@expect status 200</c> (or <c>2xx</c>), <c>@expect header content-type ~ json</c>
	/// and <c>@expect json $.items[*].name contains "lamp"</c>. The runner checks them after the response and stops at the
	/// first that does not hold.
	/// </summary>
	public IReadOnlyList<HttpExpectation> Expectations { get; }

	/// <summary><c>@no-redirect</c>: a 3xx response is the result; the runner does not follow it.</summary>
	public bool NoRedirect { get; }

	/// <summary><c>@no-cookie-jar</c>: the request neither sends nor stores cookies from the runner's cookie jar.</summary>
	public bool NoCookieJar { get; }

	/// <summary><c>@timeout 30</c> (seconds, or with a unit: <c>ms</c>, <c>s</c>, <c>m</c>): how long the request, redirects included, may take.</summary>
	public TimeSpan? Timeout { get; }
}

/// <summary>A request header as written: <c>Name: value</c>.</summary>
public sealed class HttpFileHeader
{
	internal HttpFileHeader(string name, Template value, int line)
	{
		Name = name;
		Template = value;
		Line = line;
	}

	/// <summary>The header name.</summary>
	public string Name { get; }

	/// <summary>The value as written, with its <c>{{ }}</c> references unresolved.</summary>
	public string Value => Template.Text;

	/// <summary>The 1-based line of the header.</summary>
	public int Line { get; }

	internal Template Template { get; }

	/// <inheritdoc />
	public override string ToString() => $"{Name}: {Value}";
}

/// <summary>What kind of body a request has.</summary>
public enum HttpFileBodyKind
{
	/// <summary>Text written in the file, with <c>{{ }}</c> references.</summary>
	Text,

	/// <summary><c>&lt; ./path</c>: the file's bytes, sent as they are.</summary>
	File,

	/// <summary><c>&lt;@ ./path</c>: the file's text with <c>{{ }}</c> references resolved.</summary>
	FileWithVariables,

	/// <summary>A <c>multipart/*</c> body mixing text with <c>&lt; ./path</c> lines; see <see cref="HttpFileBody.Parts"/>.</summary>
	Mixed,
}

/// <summary>One piece of a body: text, or a file included by a <c>&lt;</c> line.</summary>
public sealed class HttpFileBodyPart
{
	internal HttpFileBodyPart(HttpFileBodyKind kind, Template? text, string? filePath, string? fullPath, string? encoding, int line)
	{
		Kind = kind;
		Template = text;
		FilePath = filePath;
		FullPath = fullPath;
		Encoding = encoding;
		Line = line;
	}

	/// <summary><see cref="HttpFileBodyKind.Text"/>, <see cref="HttpFileBodyKind.File"/> or <see cref="HttpFileBodyKind.FileWithVariables"/>.</summary>
	public HttpFileBodyKind Kind { get; }

	/// <summary>The text as written, for a text part; otherwise <see langword="null"/>.</summary>
	public string? Text => Template?.Text;

	/// <summary>The file path as written, for a file part; otherwise <see langword="null"/>.</summary>
	public string? FilePath { get; }

	/// <summary>The file path resolved against the <c>.http</c> file's folder, for a file part; otherwise <see langword="null"/>.</summary>
	public string? FullPath { get; }

	/// <summary>The encoding named in <c>&lt;@latin1 ./path</c>; <see langword="null"/> means UTF-8.</summary>
	public string? Encoding { get; }

	/// <summary>The 1-based line the part starts on.</summary>
	public int Line { get; }

	internal Template? Template { get; }
}

/// <summary>A request body.</summary>
public sealed class HttpFileBody
{
	internal HttpFileBody(HttpFileBodyKind kind, IReadOnlyList<HttpFileBodyPart> parts, int line)
	{
		Kind = kind;
		Parts = parts;
		Line = line;
	}

	/// <summary>What kind of body this is.</summary>
	public HttpFileBodyKind Kind { get; }

	/// <summary>The pieces of the body: one for text and file bodies, several for <see cref="HttpFileBodyKind.Mixed"/>.</summary>
	public IReadOnlyList<HttpFileBodyPart> Parts { get; }

	/// <summary>The 1-based line the body starts on.</summary>
	public int Line { get; }

	/// <summary>The text as written, for a <see cref="HttpFileBodyKind.Text"/> body; otherwise <see langword="null"/>.</summary>
	public string? Text => Kind == HttpFileBodyKind.Text ? Parts[0].Text : null;

	/// <summary>The file path as written, for a file body; otherwise <see langword="null"/>.</summary>
	public string? FilePath => Kind is HttpFileBodyKind.File or HttpFileBodyKind.FileWithVariables ? Parts[0].FilePath : null;
}

/// <summary>A request in a <c>.http</c> file, as written: nothing is resolved until it runs.</summary>
public sealed class HttpFileRequest
{
	internal HttpFileRequest(
		int index,
		string? name,
		int line,
		string requestLine,
		string method,
		Template url,
		string? version,
		IReadOnlyList<HttpFileHeader> headers,
		HttpFileBody? body,
		HttpFileDirectives directives
	)
	{
		Index = index;
		Name = name;
		Line = line;
		RequestLine = requestLine;
		Method = method;
		Url = url;
		Version = version;
		Headers = headers;
		Body = body;
		Directives = directives;
	}

	/// <summary>The 0-based position of the request in the file.</summary>
	public int Index { get; }

	/// <summary>
	/// The name from <c># @name login</c>, or else from the separator line (<c>### login</c>);
	/// <see langword="null"/> when the request has none. Other requests reference it as <c>{{login.response...}}</c>.
	/// </summary>
	public string? Name { get; }

	/// <summary>The 1-based line of the request line.</summary>
	public int Line { get; }

	/// <summary>The request line as written, trimmed (<c>POST {{host}}/login HTTP/1.1</c>), for headings.</summary>
	public string RequestLine { get; }

	/// <summary>The method, upper case; <c>GET</c> when the request line has none.</summary>
	public string Method { get; }

	/// <summary>The URL as written, query continuation lines appended, with its <c>{{ }}</c> references unresolved.</summary>
	public string UrlTemplate => Url.Text;

	/// <summary>The HTTP version from the request line (<c>HTTP/1.1</c>, <c>HTTP/2</c>, <c>HTTP/3</c>); <see langword="null"/> when none is written.</summary>
	public string? Version { get; }

	/// <summary>The headers as written, in order.</summary>
	public IReadOnlyList<HttpFileHeader> Headers { get; }

	/// <summary>The body; <see langword="null"/> when the request has none.</summary>
	public HttpFileBody? Body { get; }

	/// <summary>The per-request settings.</summary>
	public HttpFileDirectives Directives { get; }

	internal Template Url { get; }

	/// <inheritdoc />
	public override string ToString() => RequestLine;
}
