using AwesomeAssertions;

namespace Nullean.HttpFileParser.Tests;

/// <summary><c># @expect</c> and <c># @readonly</c>: parsed, checked after each response, and enforced by a read-only run.</summary>
public class ExpectTests
{
	private static HttpFile Write(string text)
	{
		var path = Path.Combine(TempDir.Create(), "api.http");
		File.WriteAllText(path, text);
		return HttpFile.Load(path);
	}

	private static HttpFileRunOptions Host(TestServer server, bool readOnly = false) =>
		new() { Variables = new Dictionary<string, string> { ["host"] = server.BaseAddress.ToString().TrimEnd('/') }, ReadOnly = readOnly, };

	[Test]
	public void ExpectationsAndReadonlyAreParsedWithTheirLines()
	{
		var file = HttpFile.Parse(
			"""
			# @readonly
			# @expect status 2xx
			# @expect header content-type ~ "application/json"
			# @expect json $.result.tools[*].name contains "search_docs"
			// @expect json $["a b"].c equals 12
			POST https://example.com/rpc

			{}
			"""
		);

		var directives = file.Requests.Single().Directives;

		directives.ReadOnly.Should().BeTrue();
		directives
			.Expectations
			.Select(e => (e.Line, e.Text))
			.Should()
			.Equal(
				(2, "@expect status 2xx"),
				(3, "@expect header content-type ~ \"application/json\""),
				(4, "@expect json $.result.tools[*].name contains \"search_docs\""),
				(5, "@expect json $[\"a b\"].c equals 12")
			);
	}

	[Test]
	public void ARequestWithoutDirectivesHasNoneAndIsNotReadonly()
	{
		var directives = HttpFile.Parse("GET https://example.com").Requests.Single().Directives;

		directives.ReadOnly.Should().BeFalse();
		directives.Expectations.Should().BeEmpty();
	}

	[Test]
	[Arguments("# @expect\nGET https://example.com", "api.http:1: '@expect ' needs a kind: '@expect status 200', '@expect header content-type ~ json' or '@expect json $.id equals 1'")]
	[Arguments("# @expect body ok\nGET https://example.com", "api.http:1: '@expect body ok' needs a kind: '@expect status 200', '@expect header content-type ~ json' or '@expect json $.id equals 1'")]
	[Arguments("# @expect status 99\nGET https://example.com", "api.http:1: '@expect status 99' needs a status code or a class: '@expect status 200' or '@expect status 2xx'")]
	[Arguments("# @expect status 6xx\nGET https://example.com", "api.http:1: '@expect status 6xx' needs a status code or a class: '@expect status 200' or '@expect status 2xx'")]
	[Arguments("# @expect header content-type json\nGET https://example.com", "api.http:1: '@expect header content-type json' needs a header name, '~' and the text it contains: '@expect header content-type ~ json'")]
	[Arguments("# @expect json $.a is 1\nGET https://example.com", "api.http:1: '@expect json $.a is 1' needs a JSONPath, 'equals' or 'contains', and a value: '@expect json $.items[*].name contains \"lamp\"'")]
	[Arguments("# @expect json $.a equals \"open\nGET https://example.com", "api.http:1: '@expect json $.a equals \"open' needs a JSONPath, 'equals' or 'contains', and a value: '@expect json $.items[*].name contains \"lamp\"'")]
	[Arguments("# @expect json a.b equals 1\nGET https://example.com", "api.http:1: '@expect json a.b equals 1': a JSONPath starts with '$'")]
	[Arguments("# @expect status 200\n", "api.http:1: per-request directives are not followed by a request")]
	public void ReportsABadExpectationWithItsLine(string text, string expected) => ParserTests.Errors(text).Should().Equal(expected);

	[Test]
	public async Task ExpectationsThatHoldLetTheRunFinish()
	{
		await using var server = new TestServer();
		var file = Write(
			"""
			# @expect status 200
			# @expect status 2xx
			# @expect header content-type ~ JSON
			# @expect json $.user.name equals ada
			# @expect json $.user.roles[*] contains admin
			# @expect json $.user.roles contains dev
			# @expect json $.user.* contains ada
			POST {{host}}/login
			Content-Type: application/json

			{"user": "ada"}

			###
			# @expect status 4xx
			GET {{host}}/secure
			"""
		);

		var result = await HttpFileRunner.RunAsync(file, options: Host(server));

		result.Failure.Should().BeNull(result.Failure?.ToString());
		result.Exchanges.Should().HaveCount(2);
	}

	[Test]
	public async Task AnUnmetStatusStopsTheRunAtTheDirectiveAndKeepsTheResponse()
	{
		await using var server = new TestServer();
		var file = Write(
			"""
			# @name secure
			# @expect status 200
			GET {{host}}/secure

			###
			GET {{host}}/target
			"""
		);

		var result = await HttpFileRunner.RunAsync(file, options: Host(server));

		result.Failure.Should().NotBeNull();
		result.Failure.Line.Should().Be(2);
		result.Failure.Message.Should().Be("@expect status 200 failed: the response was 401 Unauthorized");
		result.Failure.Request.Name.Should().Be("secure");
		result
			.Exchanges
			.Should()
			.ContainSingle("the response that missed the expectation stays in the run, and the next request is not sent")
			.Which
			.Response
			.StatusCode
			.Should()
			.Be(401);
	}

	[Test]
	public async Task AnUnmetHeaderNamesTheHeaderAndItsValue()
	{
		await using var server = new TestServer();
		var file = Write(
			"""
			# @expect header content-type ~ xml
			# @expect header x-missing ~ a
			GET {{host}}/secure
			"""
		);

		var result = await HttpFileRunner.RunAsync(file, options: Host(server));

		result.Failure!.Message.Should().Be("@expect header content-type ~ xml failed: content-type was \"application/json; charset=utf-8\"");

		var second = await HttpFileRunner.RunAsync(Write("# @expect header x-missing ~ a\nGET {{host}}/secure"), options: Host(server));
		second.Failure!.Message.Should().Be("@expect header x-missing ~ a failed: the response has no x-missing header");
	}

	[Test]
	public async Task AnUnmetJsonExpectationNamesThePathAndWhatItSelected()
	{
		await using var server = new TestServer();

		var equals = await HttpFileRunner.RunAsync(Write("# @expect json $.user.roles[*] equals admin\nPOST {{host}}/login\n\n{}"), options: Host(server));
		var contains = await HttpFileRunner.RunAsync(Write("# @expect json $.user.roles[*] contains lamp\nPOST {{host}}/login\n\n{}"), options: Host(server));
		var nothing = await HttpFileRunner.RunAsync(Write("# @expect json $.user.missing equals 1\nPOST {{host}}/login\n\n{}"), options: Host(server));
		var notJson = await HttpFileRunner.RunAsync(Write("# @expect json $.a equals 1\nGET {{host}}/target"), options: Host(server));

		equals.Failure!.Message.Should().Be("@expect json $.user.roles[*] equals admin failed: $.user.roles[*] selected \"admin\", \"dev\"");
		contains.Failure!.Message.Should().Be("@expect json $.user.roles[*] contains lamp failed: $.user.roles[*] selected \"admin\", \"dev\"");
		nothing.Failure!.Message.Should().Be("@expect json $.user.missing equals 1 failed: $.user.missing selects nothing");
		notJson.Failure!.Message.Should().StartWith("@expect json $.a equals 1 failed: the body is not JSON");
	}

	[Test]
	public async Task AReadOnlyRunRefusesAWriteWithoutSendingIt()
	{
		await using var server = new TestServer();
		var file = Write(
			"""
			GET {{host}}/target

			###
			# @name login
			POST {{host}}/login
			Content-Type: application/json

			{"user": "ada"}

			###
			GET {{host}}/target
			"""
		);

		var result = await HttpFileRunner.RunAsync(file, options: Host(server, readOnly: true));

		result.Failure.Should().NotBeNull();
		result.Failure.Line.Should().Be(5);
		result
			.Failure
			.Message
			.Should()
			.Be("not sent: a read-only run sends only GET, HEAD and OPTIONS requests, and this is POST. Put '# @readonly' before the request if it only reads");
		result.Exchanges.Should().ContainSingle("the GET before it ran, and nothing after the refusal did");
	}

	[Test]
	public async Task AReadOnlyRunSendsAReadonlyPostAndAWriteRunSendsEveryRequest()
	{
		await using var server = new TestServer();
		var readonlyPost = Write("# @readonly\nPOST {{host}}/login\nContent-Type: application/json\n\n{}");
		var plainPost = Write("POST {{host}}/login\nContent-Type: application/json\n\n{}");

		var allowed = await HttpFileRunner.RunAsync(readonlyPost, options: Host(server, readOnly: true));
		var normal = await HttpFileRunner.RunAsync(plainPost, options: Host(server));

		allowed.Failure.Should().BeNull();
		allowed.Exchanges.Should().ContainSingle();
		normal.Failure.Should().BeNull();
		normal.Exchanges.Should().ContainSingle();
	}
}
