using AwesomeAssertions;

namespace Nullean.HttpFileParser.Tests;

/// <summary>
/// Examples transcribed from the three dialects' documentation: portable ones parse, script ones are refused.
/// Sources: https://learn.microsoft.com/aspnet/core/test/http-files, https://github.com/Huachao/vscode-restclient
/// and https://www.jetbrains.com/help/idea/exploring-http-syntax.html.
/// </summary>
public class DocumentationExampleTests
{
	private static string Shape(HttpFile file) => string.Join("\n", file.Requests.Select(r =>
		$"{r.Name ?? "-"} | {r.Method} {r.UrlTemplate} {r.Version ?? "-"} | {string.Join("; ", r.Headers)} | {r.Body?.Kind.ToString() ?? "-"}"));

	[Test]
	public void VisualStudioRequests()
	{
		var file = HttpFile.Parse("""
			GET https://localhost:7220/weatherforecast

			###

			GET https://localhost:7220/weatherforecast?date=2023-05-11&location=98006

			###

			GET https://localhost:7220/weatherforecast HTTP/3

			###
			""");
		Shape(file).Should().Be("""
			- | GET https://localhost:7220/weatherforecast - |  | -
			- | GET https://localhost:7220/weatherforecast?date=2023-05-11&location=98006 - |  | -
			- | GET https://localhost:7220/weatherforecast HTTP/3 |  | -
			""".ReplaceLineEndings("\n"));
	}

	[Test]
	public void VisualStudioHeadersAndBody()
	{
		var file = HttpFile.Parse("""
			GET https://localhost:7220/weatherforecast
			Date: Wed, 27 Apr 2023 07:28:00 GMT

			###

			GET https://localhost:7220/weatherforecast
			Cache-Control: max-age=604800
			Age: 100

			###

			POST https://localhost:7220/weatherforecast
			Content-Type: application/json
			Accept-Language: en-US,en;q=0.5

			{
			    "date": "2023-05-10",
			    "temperatureC": 30,
			    "summary": "Warm"
			}

			###
			""");
		Shape(file).Should().Be("""
			- | GET https://localhost:7220/weatherforecast - | Date: Wed, 27 Apr 2023 07:28:00 GMT | -
			- | GET https://localhost:7220/weatherforecast - | Cache-Control: max-age=604800; Age: 100 | -
			- | POST https://localhost:7220/weatherforecast - | Content-Type: application/json; Accept-Language: en-US,en;q=0.5 | Text
			""".ReplaceLineEndings("\n"));
		file.Requests[2].Body!.Text.Should().Be(/*lang=json,strict*/ "{\n    \"date\": \"2023-05-10\",\n    \"temperatureC\": 30,\n    \"summary\": \"Warm\"\n}");
	}

	[Test]
	public void VisualStudioVariablesAndRequestVariables()
	{
		var file = HttpFile.Parse("""
			@hostname=localhost
			@port=44320
			@host={{hostname}}:{{port}}
			GET https://{{host}}/api/search/tool

			###

			# @name login

			POST {{TodoApi_HostAddress}}/users/token
			Content-Type: application/json

			{
			  "username": "{{myusername}}",
			}

			###

			GET {{TodoApi_HostAddress}}/todos
			Authorization: Bearer {{login.response.body.$.token}}

			###
			""");
		Shape(file).Should().Be("""
			- | GET https://{{host}}/api/search/tool - |  | -
			login | POST {{TodoApi_HostAddress}}/users/token - | Content-Type: application/json | Text
			- | GET {{TodoApi_HostAddress}}/todos - | Authorization: Bearer {{login.response.body.$.token}} | -
			""".ReplaceLineEndings("\n"));
		file.Validate(variables: new Dictionary<string, string> { ["TodoApi_HostAddress"] = "x", ["myusername"] = "u" }).Should().BeEmpty();
	}

	[Test]
	public void VisualStudioDynamicValues()
	{
		var file = HttpFile.Parse("""
			GET https://localhost:7220/weatherforecast
			X-CUSTOM: {{$datetime "dd-MM-yyyy"}}
			X-ISO8601: {{$datetime iso8601}}
			X-ISO8601L: {{$localDatetime iso8601}}
			X-RFC1123: {{$datetime rfc1123}}
			X-RFC1123L: {{$localDatetime rfc1123}}

			###

			GET https://localhost:7220/weatherforecast
			X-Custom-Minus-1-Year: {{$datetime "dd-MM-yyyy" -1 y}}
			X-RFC1123-Plus-1-Day: {{$datetime rfc1123 1 d}}
			X-Timestamp-Plus-1-Year: {{$timestamp 1 y}}

			###

			GET https://localhost:7220/weatherforecast
			X-CorrelationId: {{$guid}}

			###

			GET {{HostAddress}}{{Path}}
			X-UserName: {{$processEnv USERNAME}}

			###

			GET {{HostAddress}}{{Path}}
			X-UserName: {{$dotenv USERNAME}}

			###

			GET {{HostAddress}}{{Path}}
			X-API-KEY: {{ApiKeyDev}}
			""");
		file.Requests.Should().HaveCount(6);
		file.Requests[1].Headers.Select(h => h.Name).Should().Equal("X-Custom-Minus-1-Year", "X-RFC1123-Plus-1-Day", "X-Timestamp-Plus-1-Year");
	}

	[Test]
	public void VisualStudioNamesRequestsWithoutAMethod()
	{
		var file = HttpFile.Parse("""
			# @name login
			https://contoso.com/api/login HTTP/1.1
			###
			// @name login
			https://contoso.com/api/login HTTP/1.1
			""");
		Shape(file).Should().Be("""
			login | GET https://contoso.com/api/login HTTP/1.1 |  | -
			login | GET https://contoso.com/api/login HTTP/1.1 |  | -
			""".ReplaceLineEndings("\n"));
	}

	[Test]
	public void VisualStudioRefusesXPath() =>
		ParserTests.Errors("GET https://example.com\nX-Token: {{login.response.body./token}}")
			.Should().Equal("api.http:2: XPath ('/token') in '{{login.response.body./token}}' is not supported; use JSONPath ('$.name') for JSON bodies or '*' for the whole body");

	[Test]
	public void RestClientRequests()
	{
		var file = HttpFile.Parse("""
			https://example.com/comments/1

			###

			GET https://example.com/comments
			    ?page=2
			    &pageSize=10

			###

			POST https://example.com/comments HTTP/1.1
			Content-Type: application/xml
			Authorization: token xxx

			< ./demo.xml

			###

			POST https://api.example.com/login HTTP/1.1
			Content-Type: application/x-www-form-urlencoded

			name=foo
			&password=bar
			""");
		Shape(file).Should().Be("""
			- | GET https://example.com/comments/1 - |  | -
			- | GET https://example.com/comments?page=2&pageSize=10 - |  | -
			- | POST https://example.com/comments HTTP/1.1 | Content-Type: application/xml; Authorization: token xxx | File
			- | POST https://api.example.com/login HTTP/1.1 | Content-Type: application/x-www-form-urlencoded | Text
			""".ReplaceLineEndings("\n"));
	}

	[Test]
	public void RestClientVariables()
	{
		var file = HttpFile.Parse("""
			@hostname = api.example.com
			@port = 8080
			@host = {{hostname}}:{{port}}
			@contentType = application/json
			@createdAt = {{$datetime iso8601}}
			@modifiedBy = {{$processEnv USERNAME}}

			###

			# @name createComment
			POST https://{{host}}/comments HTTP/1.1
			Content-Type: {{contentType}}

			{
			    "content": "fake content",
			    "created_at": "{{createdAt}}",
			    "modified_by": "{{modifiedBy}}"
			}

			###

			@commentId = {{createComment.response.body.$.id}}

			# @name getCommentReplies
			GET https://{{host}}/comments/{{commentId}}/replies HTTP/1.1
			Accept: application/json

			###

			# @name getFirstReply
			GET https://{{host}}/comments/{{commentId}}/replies/{{getCommentReplies.response.body.$[0].id}}
			""");
		file.Variables.Select(v => v.Name).Should().Equal("hostname", "port", "host", "contentType", "createdAt", "modifiedBy", "commentId");
		file.Requests.Select(r => r.Name).Should().Equal("createComment", "getCommentReplies", "getFirstReply");
		file.Validate().Should().BeEmpty();
	}

	[Test]
	public void RestClientDynamicValues()
	{
		var file = HttpFile.Parse("""
			POST https://api.example.com/comments HTTP/1.1
			Content-Type: application/xml
			Date: {{$datetime rfc1123}}

			{
			    "user_name": "{{$dotenv USERNAME}}",
			    "request_id": "{{$guid}}",
			    "updated_at": "{{$timestamp}}",
			    "created_at": "{{$timestamp -1 d}}",
			    "review_count": "{{$randomInt 5 200}}",
			    "custom_date": "{{$datetime 'yyyy-MM-dd'}}",
			    "local_custom_date": "{{$localDatetime 'yyyy-MM-dd'}}"
			}
			""");
		file.Requests.Single().Body!.Kind.Should().Be(HttpFileBodyKind.Text);
	}

	[Test]
	public void RestClientDirectivesAndRefusals()
	{
		var file = HttpFile.Parse("""
			# @note
			# @no-redirect
			# @no-cookie-jar
			GET https://example.com/api
			""");
		file.Requests[0].Directives.NoRedirect.Should().BeTrue();
		file.Requests[0].Directives.NoCookieJar.Should().BeTrue();

		ParserTests.Errors("""
			# @prompt username
			# @prompt refCode Your reference code display on webpage
			# @prompt otp Your one-time password in your mailbox
			POST https://{{host}}/verify-otp/{{refCode}} HTTP/1.1
			Content-Type: {{contentType}}

			{{otp}}
			""").Should().HaveCount(3).And.AllSatisfy(e => e.Should().Contain("prompt variables ('@prompt') are not supported"));

		ParserTests.Errors("""
			# @name getFirstReply
			GET {{baseUrl}}/comments/{{commentId}}/replies/{{getReplies.response.body.//reply[1]/@id}}
			""").Should().Equal("api.http:2: XPath ('//reply[1]/@id') in '{{getReplies.response.body.//reply[1]/@id}}' is not supported; use JSONPath ('$.name') for JSON bodies or '*' for the whole body");

		ParserTests.Errors("""
			POST https://api.github.com/graphql
			Content-Type: application/json
			Authorization: Bearer xxx
			X-REQUEST-TYPE: GraphQL

			query ($name: String!, $owner: String!) {
			  repository(name: $name, owner: $owner) {
			    name
			  }
			}
			""").Should().Equal("api.http:4: GraphQL requests (X-REQUEST-TYPE: GraphQL) are not supported; send the query as a JSON body with Content-Type: application/json");

		ParserTests.Errors("curl -X POST https://example.com/comments -H 'Content-Type: application/json' -d '{}'")
			.Should().Equal("api.http:1: cURL commands are not supported; write the request as 'METHOD URL' followed by its headers");
	}

	[Test]
	public void JetBrainsPortableForms()
	{
		var file = HttpFile.Parse("""
			### A basic request
			GET http://example.com/a/

			###
			// A basic request
			https://example.com/a/

			### A second request using the GET method
			https://example.com:8080/api/html/get?id=123&value=content

			###
			// Using line breaks with indent
			GET http://example.com:8080
			    /api
			    /html
			    /get
			    ?id=123
			    &value=content

			###
			GET https://example.com:8080/api/get/html?
			    firstname=John&
			    lastname=Doe&
			    planet=Tatooine&
			    town=Freetown

			###
			// The request body is provided in place
			POST https://example.com:8080/api/html/post HTTP/1.1
			Content-Type: application/json
			Cookie: key=first-value

			{ "key" : "value", "list": [1, 2, 3] }

			###
			// The request body is read from a file
			POST https://example.com:8080/api/html/post
			Content-Type: application/json

			< ./input.json

			###
			// @no-redirect
			example.com/status/301

			###
			# @timeout 600
			GET example.com/api
			""");
		Shape(file).Should().Be("""
			A basic request | GET http://example.com/a/ - |  | -
			- | GET https://example.com/a/ - |  | -
			A second request using the GET method | GET https://example.com:8080/api/html/get?id=123&value=content - |  | -
			- | GET http://example.com:8080/api/html/get?id=123&value=content - |  | -
			- | GET https://example.com:8080/api/get/html?firstname=John&lastname=Doe&planet=Tatooine&town=Freetown - |  | -
			- | POST https://example.com:8080/api/html/post HTTP/1.1 | Content-Type: application/json; Cookie: key=first-value | Text
			- | POST https://example.com:8080/api/html/post - | Content-Type: application/json | File
			- | GET example.com/status/301 - |  | -
			- | GET example.com/api - |  | -
			""".ReplaceLineEndings("\n"));
		file.Requests[7].Directives.NoRedirect.Should().BeTrue();
		file.Requests[8].Directives.Timeout.Should().Be(TimeSpan.FromSeconds(600));
	}

	[Test]
	public void JetBrainsMultipart()
	{
		var file = HttpFile.Parse("""
			POST https://example.com/api/upload HTTP/1.1
			Content-Type: multipart/form-data; boundary=boundary

			--boundary
			Content-Disposition: form-data; name="first"; filename="input.txt"

			< ./input.txt
			--boundary
			Content-Disposition: form-data; name="second"; filename="input-second.txt"

			Text
			--boundary
			Content-Disposition: form-data; name="third";

			< ./input.txt
			--boundary--
			""");
		file.Requests[0].Body!.Kind.Should().Be(HttpFileBodyKind.Mixed);
		file.Requests[0].Body!.Parts.Count(p => p.Kind == HttpFileBodyKind.File).Should().Be(2);
	}

	[Test]
	public void JetBrainsScriptsAreRefused()
	{
		ParserTests.Errors("""
			GET https://httpbin.org/get

			> {%
			    client.global.set("my_cookie", response.headers.valuesOf("Set-Cookie")[0]);
			%}
			""").Should().Equal("api.http:3: JetBrains response handler scripts ('> {% ... %}') are not supported: they run JavaScript");

		ParserTests.Errors("""
			GET https://httpbin.org/get

			> /path/to/responseHandler.js
			""").Should().Equal("api.http:3: JetBrains response handler files ('> handler.js') are not supported: they run JavaScript");

		ParserTests.Errors("""
			< {%
			    const start = Date.now();
			    await sleep(10000);
			%}
			GET https://examples.http-client.intellij.net/anything

			> {%
			    console.log("Post-script: Script execution complete.");
			%}
			""").Should().Equal(
			"api.http:1: JetBrains pre-request scripts ('< {% ... %}') are not supported: they run JavaScript",
			"api.http:7: JetBrains response handler scripts ('> {% ... %}') are not supported: they run JavaScript");

		ParserTests.Errors("""
			POST https://httpbin.org/post
			Content-Type: application/json

			{
			  "id": 999,
			  "value": "content"
			}

			>> myFolder/myFile.json
			""").Should().Equal("api.http:9: response redirection ('>> file') is not supported; the run result holds the response");

		ParserTests.Errors("""
			POST https://httpbin.org/post
			Content-Type: application/json

			{
			  "id": 999,
			  "value": "content"
			}

			> {{$projectRoot}}/handler.js
			>>! {{$historyFolder}}/myFile.json
			""").Should().Equal(
			"api.http:9: JetBrains response handler files ('> handler.js') are not supported: they run JavaScript",
			"api.http:10: response redirection ('>>! file') is not supported; the run result holds the response");
	}
}
