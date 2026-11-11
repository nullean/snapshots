using AwesomeAssertions;

namespace Nullean.HttpFileParser.Tests;

public class ParserTests
{
	internal static string[] Errors(string text, string path = "api.http")
	{
		var act = () => HttpFile.Parse(text, path);
		return [.. act.Should().Throw<HttpFileParseException>().Which.Errors.Select(e => e.ToString())];
	}

	[Test]
	public void SeparatesRequestsOnThreeOrMoreHashes()
	{
		var file = HttpFile.Parse(
			"""
			GET https://example.com/a

			###

			POST https://example.com/b
			#####
			https://example.com/c
			"""
		);
		file
			.Requests
			.Select(r => (r.Method, r.UrlTemplate, r.Line))
			.Should()
			.Equal(("GET", "https://example.com/a", 1), ("POST", "https://example.com/b", 5), ("GET", "https://example.com/c", 7));
	}

	[Test]
	public void NamesRequests()
	{
		var file = HttpFile.Parse(
			"""
			# @name login
			POST https://example.com/login

			###
			// @name = other
			GET https://example.com/other

			### Get the list
			GET https://example.com/list

			### fromSeparator
			// @name fromComment
			GET https://example.com/list
			###
			GET https://example.com/unnamed
			"""
		);
		file.Requests.Select(r => r.Name).Should().Equal("login", "other", "Get the list", "fromComment", null);
	}

	[Test]
	public void ParsesTheRequestLine()
	{
		var file = HttpFile.Parse(
			"""
			GET https://example.com/a HTTP/1.1
			###
			https://example.com/b
			###
			delete https://example.com/c HTTP/2
			###
			PATCH /relative HTTP/3
			"""
		);
		file
			.Requests
			.Select(r => (r.Method, r.UrlTemplate, r.Version))
			.Should()
			.Equal(
				("GET", "https://example.com/a", "HTTP/1.1"),
				("GET", "https://example.com/b", null),
				("DELETE", "https://example.com/c", "HTTP/2"),
				("PATCH", "/relative", "HTTP/3")
			);
		file.Requests[0].RequestLine.Should().Be("GET https://example.com/a HTTP/1.1");
	}

	[Test]
	public void AppendsQueryContinuationLines()
	{
		var file = HttpFile.Parse(
			"""
			GET https://example.com/comments
			    ?page=2
			    &pageSize=10
			Accept: application/json
			###
			GET https://example.com:8080
			    /api
			    /html
			    ?id=123
			"""
		);
		file.Requests[0].UrlTemplate.Should().Be("https://example.com/comments?page=2&pageSize=10");
		file.Requests[0].Headers.Should().ContainSingle().Which.ToString().Should().Be("Accept: application/json");
		file.Requests[1].UrlTemplate.Should().Be("https://example.com:8080/api/html?id=123");
	}

	[Test]
	public void ReadsHeadersThenTheBody()
	{
		var file = HttpFile.Parse(
			"""
			POST https://example.com/comments
			Content-Type: application/json
			# a comment between headers
			Authorization: Bearer {{token}}

			{
			  "name": "sample"
			}



			###
			"""
		);
		var request = file.Requests.Single();
		request
			.Headers
			.Select(h => (h.Name, h.Value, h.Line))
			.Should()
			.Equal(("Content-Type", "application/json", 2), ("Authorization", "Bearer {{token}}", 4));
		request.Body!.Kind.Should().Be(HttpFileBodyKind.Text);
		request.Body.Text.Should().Be(/*lang=json,strict*/ "{\n  \"name\": \"sample\"\n}");
		request.Body.Line.Should().Be(6);
	}

	[Test]
	public void HandlesCrLfLineEndings()
	{
		var file = HttpFile.Parse("POST https://example.com\r\nContent-Type: text/plain\r\n\r\nline one\r\nline two\r\n");
		file.Requests[0].Body!.Text.Should().Be("line one\nline two");
	}

	[Test]
	public void ReadsFileBodies()
	{
		var path = Path.Combine(Path.GetTempPath(), "dir", "api.http");
		var file = HttpFile.Parse(
			"""
			POST https://example.com
			Content-Type: application/xml

			< ./demo.xml
			###
			POST https://example.com

			<@ ./template.json
			###
			POST https://example.com

			<@latin1 ./latin.txt
			""",
			path
		);
		file.Requests[0].Body!.Kind.Should().Be(HttpFileBodyKind.File);
		file.Requests[0].Body!.FilePath.Should().Be("./demo.xml");
		file.Requests[0].Body!.Parts[0].FullPath.Should().Be(Path.Combine(Path.GetTempPath(), "dir", "demo.xml"));
		file.Requests[1].Body!.Kind.Should().Be(HttpFileBodyKind.FileWithVariables);
		file.Requests[2].Body!.Parts[0].Encoding.Should().Be("latin1");
	}

	[Test]
	public void MixesTextAndFilesInMultipartBodies()
	{
		var file = HttpFile.Parse(
			"""
			POST https://example.com/upload
			Content-Type: multipart/form-data; boundary=b

			--b
			Content-Disposition: form-data; name="first"; filename="input.txt"

			< ./input.txt
			--b--
			"""
		);
		var body = file.Requests[0].Body!;
		body.Kind.Should().Be(HttpFileBodyKind.Mixed);
		body.Parts.Select(p => p.Kind).Should().Equal(HttpFileBodyKind.Text, HttpFileBodyKind.File, HttpFileBodyKind.Text);
		body.Parts[0].Text.Should().Be("--b\r\nContent-Disposition: form-data; name=\"first\"; filename=\"input.txt\"\r\n\r\n");
		body.Parts[1].Line.Should().Be(7);
		body.Parts[2].Text.Should().Be("\r\n--b--");
	}

	[Test]
	public void ReadsFileVariables()
	{
		var file = HttpFile.Parse(
			"""
			@hostname = localhost
			@port=44320
			@host = {{hostname}}:{{port}}
			GET https://{{host}}/api
			###
			@later = 1
			GET https://{{host}}/{{later}}
			"""
		);
		file
			.Variables
			.Select(v => (v.Name, v.Value, v.Line))
			.Should()
			.Equal(("hostname", "localhost", 1), ("port", "44320", 2), ("host", "{{hostname}}:{{port}}", 3), ("later", "1", 6));
		file.Requests.Should().HaveCount(2);
	}

	[Test]
	public void ReadsDirectives()
	{
		var file = HttpFile.Parse(
			"""
			# @no-redirect
			// @no-cookie-jar
			# @timeout 5
			# @note unknown directives are comments
			# @connection-timeout 2 m
			GET https://example.com
			###
			# @timeout 500 ms
			GET https://example.com
			###
			// @timeout 2 m
			GET https://example.com
			"""
		);
		var d = file.Requests[0].Directives;
		(d.NoRedirect, d.NoCookieJar, d.Timeout).Should().Be((true, true, TimeSpan.FromSeconds(5)));
		file.Requests[1].Directives.Timeout.Should().Be(TimeSpan.FromMilliseconds(500));
		file.Requests[1].Directives.NoRedirect.Should().BeFalse();
		file.Requests[2].Directives.Timeout.Should().Be(TimeSpan.FromMinutes(2));
	}

	[Test]
	public void AcceptsEveryDynamicValue()
	{
		var file = HttpFile.Parse(
			"""
			GET https://example.com
			A: {{$guid}} {{$uuid}} {{$random.uuid}}
			B: {{$randomInt}} {{$randomInt 5 200}}
			C: {{$datetime rfc1123}} {{$datetime iso8601 1 d}} {{$datetime "dd-MM-yyyy" -1 y}} {{$datetime 'HH:mm' 3 h}}
			D: {{$localDatetime iso8601}} {{$timestamp}} {{$timestamp -3 h}} {{$isoTimestamp}} {{$timestamp 1y}}
			E: {{$processEnv HOME}} {{$processEnv %name}} {{$env.HOME}} {{$dotenv KEY}} {{$secret Shop:ApiKey}}
			F: {{login.response.body.$.token}} {{login.response.body.*}} {{login.response.headers.Location}} {{login.request.body.$.a["b c"][0]}}
			"""
		);
		file.Requests[0].Headers.Should().HaveCount(6);
	}

	[Test]
	[Arguments("GET https://example.com\nA: {{$nope}}", "api.http:2: unknown dynamic variable '$nope' in '{{$nope}}'")]
	[Arguments("GET https://example.com\nA: {{$randomInt 5}}", "api.http:2: '{{$randomInt 5}}': $randomInt takes no arguments or two integers, '$randomInt min max'")]
	[Arguments("GET https://example.com\nA: {{$randomInt 5 5}}", "api.http:2: '{{$randomInt 5 5}}': $randomInt needs min below max (max is exclusive)")]
	[Arguments("GET https://example.com\nA: {{$datetime rfc999}}", "api.http:2: '{{$datetime rfc999}}': the format must be rfc1123, iso8601 or a custom format in quotes, not 'rfc999'")]
	[Arguments("GET https://example.com\nA: {{$timestamp 1 parsec}}", "api.http:2: $timestamp: expected an offset like '1 d' or '-3 h' (units ms, s, m, h, d, w, M, y), got '1 parsec'")]
	[Arguments("GET https://example.com\nA: {{$processEnv}}", "api.http:2: '{{$processEnv}}': $processEnv takes one name, '$processEnv NAME'")]
	[Arguments("GET https://example.com\nA: {{$guid x}}", "api.http:2: $guid takes no arguments")]
	[Arguments("GET https://example.com\nA: {{}}", "api.http:2: empty variable reference '{{}}'")]
	[Arguments("GET https://example.com\nA: {{a b}}", "api.http:2: invalid variable reference '{{a b}}': a variable name cannot contain spaces or braces")]
	[Arguments("GET https://example.com\nA: {{login.response.body./token}}", "api.http:2: XPath ('/token') in '{{login.response.body./token}}' is not supported; use JSONPath ('$.name') for JSON bodies or '*' for the whole body")]
	[Arguments("GET https://example.com\nA: {{login.response.body.$..x}}", "api.http:2: invalid JSONPath '$..x' in '{{login.response.body.$..x}}': a property name is missing at position 2")]
	[Arguments("GET https://example.com\nA: {{login.response.body.token}}", "api.http:2: 'token' in '{{login.response.body.token}}' is not a body selector: use '*' for the whole body or a JSONPath starting with '$'")]
	[Arguments("GET https://example.com\nnot a header", "api.http:2: expected a header 'Name: value' (or a blank line before the body), got 'not a header'")]
	[Arguments("@ = 1\nGET https://example.com", "api.http:1: expected a variable definition '@name = value', got '@ = 1'")]
	[Arguments("@novalue\nGET https://example.com", "api.http:1: expected a variable definition '@name = value', got '@novalue'")]
	[Arguments("# @name\nGET https://example.com", "api.http:1: '@name' needs a name, e.g. '# @name login'")]
	[Arguments("# @name a.b\nGET https://example.com", "api.http:1: request name 'a.b' cannot contain spaces or dots")]
	[Arguments("# @name orphan\n", "api.http:1: '@name orphan' is not followed by a request")]
	[Arguments("# @timeout soon\nGET https://example.com", "api.http:1: '@timeout soon' needs a positive number of seconds, optionally with a unit: '@timeout 30', '@timeout 500 ms', '@timeout 2 m'")]
	[Arguments("GET https://example.com HTTP/9", "api.http:1: unsupported HTTP version 'HTTP/9'; use HTTP/1.1, HTTP/2 or HTTP/3")]
	[Arguments("POST https://example.com\n\n< ./a.json\nmore", "api.http:4: a file body ('< ./path') must be the only line of the body; to mix text and files use a multipart/* Content-Type")]
	[Arguments("POST https://example.com\n\n<@nonsense-encoding ./a.json", "api.http:3: unknown encoding 'nonsense-encoding' in '<@nonsense-encoding'; use a name such as utf-8, utf-16 or latin1")]
	public void ReportsSyntaxErrorsWithTheirLine(string text, string expected) => Errors(text).Should().Equal(expected);

	[Test]
	[Arguments("GET https://example.com\n\n> {%\n  client.global.set(\"a\", response.body.a);\n%}", "api.http:3: JetBrains response handler scripts ('> {% ... %}') are not supported: they run JavaScript")]
	[Arguments("GET https://example.com\n> {% client.test(); %}", "api.http:2: JetBrains response handler scripts ('> {% ... %}') are not supported: they run JavaScript")]
	[Arguments("GET https://example.com\n\n> ./handler.js", "api.http:3: JetBrains response handler files ('> handler.js') are not supported: they run JavaScript")]
	[Arguments("< {%\n  request.variables.set('a', 1)\n%}\nGET https://example.com", "api.http:1: JetBrains pre-request scripts ('< {% ... %}') are not supported: they run JavaScript")]
	[Arguments("< ./setup.js\nGET https://example.com", "api.http:1: JetBrains pre-request script files ('< script.js') are not supported: they run JavaScript")]
	[Arguments("POST https://example.com\n\n{}\n\n>> out.json", "api.http:5: response redirection ('>> file') is not supported; the run result holds the response")]
	[Arguments("POST https://example.com\n\n{}\n\n>>! out.json", "api.http:5: response redirection ('>>! file') is not supported; the run result holds the response")]
	[Arguments("GET https://example.com\nAuthorization: {{$aadToken}}", "api.http:2: '{{$aadToken}}' is not supported: Azure AD tokens need an interactive sign-in. Provide the token through HttpFileRunOptions.Secrets and reference it as '{{$secret Name}}'")]
	[Arguments("GET https://example.com\nAuthorization: {{$aadV2Token appOnly}}", "api.http:2: '{{$aadV2Token appOnly}}' is not supported: Azure AD tokens need an interactive sign-in. Provide the token through HttpFileRunOptions.Secrets and reference it as '{{$secret Name}}'")]
	[Arguments("# @prompt username\nGET https://example.com", "api.http:1: prompt variables ('@prompt') are not supported: a run cannot ask questions. Pass the value with HttpFileRunOptions.Variables")]
	[Arguments("POST https://example.com/graphql\nX-REQUEST-TYPE: GraphQL\n\nquery { a }", "api.http:2: GraphQL requests (X-REQUEST-TYPE: GraphQL) are not supported; send the query as a JSON body with Content-Type: application/json")]
	[Arguments("curl -X POST https://example.com", "api.http:1: cURL commands are not supported; write the request as 'METHOD URL' followed by its headers")]
	[Arguments("WEBSOCKET ws://example.com/ws", "api.http:1: WEBSOCKET requests are not supported; only HTTP requests are")]
	[Arguments("GRPC localhost:50051/Service/Method", "api.http:1: GRPC requests are not supported; only HTTP requests are")]
	[Arguments("run #login\nGET https://example.com", "api.http:1: JetBrains 'run' statements are not supported")]
	[Arguments("import ./other.http\nGET https://example.com", "api.http:1: JetBrains 'import' statements are not supported")]
	public void RefusesWhatItDoesNotSupport(string text, string expected) => Errors(text).Should().Equal(expected);

	[Test]
	public void ReportsEveryErrorAtOnce()
	{
		var errors = Errors(
			"""
			# @prompt user
			GET https://example.com
			A: {{$nope}}
			not a header

			###
			curl https://example.com
			###
			GET https://example.com

			> {% client.test() %}
			"""
		);
		errors.Should().Equal(
			"api.http:1: prompt variables ('@prompt') are not supported: a run cannot ask questions. Pass the value with HttpFileRunOptions.Variables",
			"api.http:3: unknown dynamic variable '$nope' in '{{$nope}}'",
			"api.http:4: expected a header 'Name: value' (or a blank line before the body), got 'not a header'",
			"api.http:7: cURL commands are not supported; write the request as 'METHOD URL' followed by its headers",
			"api.http:11: JetBrains response handler scripts ('> {% ... %}') are not supported: they run JavaScript"
		);
	}

	[Test]
	public void TheExceptionMessageListsEveryError()
	{
		var act = () => HttpFile.Parse("GET https://example.com\nA: {{$nope}}\nB: {{}}", "x.http");
		act
			.Should()
			.Throw<HttpFileParseException>()
			.Which
			.Message
			.Should()
			.Be(
				$"2 errors:{Environment.NewLine}x.http:2: unknown dynamic variable '$nope' in '{{{{$nope}}}}'{Environment.NewLine}x.http:3: empty variable reference '{{{{}}}}'"
			);
	}

	[Test]
	public void ReportsTheLineOfAReferenceInsideAMultilineBody()
	{
		Errors("POST https://example.com\n\n{\n  \"a\": 1,\n  \"b\": \"{{$nope}}\"\n}")
			.Should()
			.Equal("api.http:5: unknown dynamic variable '$nope' in '{{$nope}}'");
	}

	[Test]
	public void LeavesUnterminatedBracesAlone()
	{
		var file = HttpFile.Parse("POST https://example.com\n\n{{ not closed\n{ \"a\": {\"b\": 1}}");
		file.Requests[0].Body!.Text.Should().Be("{{ not closed\n{ \"a\": {\"b\": 1}}");
	}

	[Test]
	public void AnEmptyFileHasNoRequests()
	{
		HttpFile.Parse("").Requests.Should().BeEmpty();
		HttpFile.Parse("# only a comment\n###\n\n").Requests.Should().BeEmpty();
	}
}
