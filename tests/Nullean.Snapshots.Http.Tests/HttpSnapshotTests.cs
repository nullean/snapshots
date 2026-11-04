using System.Net;
using AwesomeAssertions;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>Snapshots of one <see cref="HttpResponseMessage"/>, from a real server.</summary>
public class HttpSnapshotTests
{
	private static TestServer Server => TestServer.Shared;

	private static HttpClient Client(bool decompress = true) =>
		new(new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None,
		})
		{ BaseAddress = Server.BaseAddress };

	[Test]
	public async Task RendersARealResponse()
	{
		using var client = Client();
		using var response = await client.PostAsync("/api/created?draft=1", new StringContent("{}"));

		var text = await HttpSnapshot.RenderAsync(response, new HttpSnapshotOptions { Headers = ["X-Request-Id", "Authorization"] });

		text.Should().Be("""
			## POST /api/created?draft=1
			status: 201 Created
			content-type: application/json; charset=utf-8
			location: /api/orders/3
			x-request-id: r-1

			{
			  "id": 3
			}

			""".Replace("\r\n", "\n", StringComparison.Ordinal));
	}

	[Test]
	public async Task MatchesItsSnapshot()
	{
		using var client = Client();
		using var response = await client.GetAsync("/api/orders");

		await response.Should().MatchHttpSnapshot();
	}

	[Test]
	public async Task AGzipBodyReadsAsTheDecompressedOne()
	{
		using var plain = Client(decompress: true);
		using var raw = Client(decompress: false);
		using var decompressed = await plain.GetAsync("/api/gzip");
		using var encoded = await raw.GetAsync("/api/gzip");
		encoded.Content.Headers.ContentEncoding.Should().Equal("gzip");

		var a = await HttpSnapshot.RenderAsync(decompressed);
		var b = await HttpSnapshot.RenderAsync(encoded);

		b.Should().Be(a);
		a.Should().Be("## GET /api/gzip\nstatus: 200 OK\ncontent-type: application/json; charset=utf-8\n\n{\n  \"zipped\": true\n}\n");
	}

	[Test]
	public async Task AnUnknownEncodingIsBinary()
	{
		using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
		response.Content.Headers.ContentType = new("application/json");
		response.Content.Headers.ContentEncoding.Add("zstd");

		(await HttpSnapshot.RenderAsync(response)).Should().Be("## response\nstatus: 200 OK\ncontent-type: application/json\n\nbinary: 3 bytes\n");
	}

	[Test]
	public async Task AResponseWithoutARequestIsHeadedResponse()
	{
		using var response = new HttpResponseMessage(HttpStatusCode.NoContent);

		(await HttpSnapshot.RenderAsync(response)).Should().Be("## response\nstatus: 204 No Content\n");
	}

	[Test]
	public async Task OnlyAnHttpResponseMessageCanMatch()
	{
		var act = async () => await "nope".As<object>().Should().MatchHttpSnapshot();

		(await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Be("Expected an HttpResponseMessage, but found String.");
	}

	[Test]
	public void TextThatIsNotResponsesFailsBeforeAnythingIsWritten() =>
		FluentActions.Invoking(() => "garbage".Should().MatchHttpSnapshot())
			.Should().Throw<Exception>().WithMessage("Expected HTTP responses text, but: Line 1: a section starts with '## ' and the request.");

	[Test]
	public void ResponsesTextMatchesItsSnapshot() =>
		"## GET /\nstatus: 200 OK\ncontent-type: text/plain\n\n> hello\n".Should().MatchHttpSnapshot();
}
