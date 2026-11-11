using System.Text;
using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>The <c>.responses</c> format: rendering, parsing and comparing.</summary>
public class HttpFormatTests
{
	private static readonly IReadOnlySet<string> Defaults = HttpResponseText.Chosen(null);

	private static HttpResponseSection Section(
		string heading,
		int status = 200,
		string? reason = "OK",
		string? body = null,
		string? mediaType = "application/json",
		string? charSet = "utf-8",
		params (string Name, string Value)[] headers
	) =>
		new(
			heading,
			status,
			reason,
			[.. headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value))],
			body is null ? [] : Encoding.UTF8.GetBytes(body),
			mediaType,
			charSet
		);

	private static string Render(IReadOnlySet<string>? chosen = null, params HttpResponseSection[] sections)
	{
		var text = new StringBuilder();
		foreach (var section in sections)
			HttpResponseText.Append(text, section, chosen ?? Defaults);
		return text.ToString();
	}

	private static string Render(params HttpResponseSection[] sections) => Render(null, sections);

	private static readonly HttpResponseSection Login = Section(
		"login: POST {{host}}/api/login",
		body: /*lang=json,strict*/ """{"token":"abc","user":{"name":"ada"}}""",
		headers: [("Content-Type", "application/json; charset=utf-8"), ("Date", "Tue, 03 Nov 2026 10:00:00 GMT")]
	);

	private static readonly HttpResponseSection Orders = Section("GET {{host}}/api/orders", 404, "Not Found", mediaType: null, charSet: null);

	[Test]
	public void RendersTheDocumentedShape() =>
		Render(Login, Orders)
			.Should()
			.Be(
				"""
			## login: POST {{host}}/api/login
			status: 200 OK
			content-type: application/json; charset=utf-8

			{
			  "token": "abc",
			  "user": {
			    "name": "ada"
			  }
			}

			## GET {{host}}/api/orders
			status: 404 Not Found

			""".Replace(
					"\r\n",
					"\n",
					StringComparison.Ordinal
				)
			);

	[Test]
	public void HeadersAreChosenLowerCasedSortedAndJoined()
	{
		var chosen = HttpResponseText.Chosen(["X-Request-Id", "VARY", "Authorization", "Set-Cookie", "cookie", "Proxy-Authorization"]);
		var text = Render(
			chosen,
			Section(
				"GET /",
				body: null,
				headers: [
					("Vary", "Accept"),
					("X-Request-Id", "r-1"),
					("Location", "/next"),
					("Vary", "Origin"),
					("Authorization", "Bearer secret"),
					("Set-Cookie", "session=secret"),
					("Cookie", "a=secret"),
					("Proxy-Authorization", "Basic secret"),
					("Content-Encoding", "gzip"),
					("Server", "Kestrel"),
				]
			)
		);

		text.Should().Be("## GET /\nstatus: 200 OK\nlocation: /next\nvary: Accept, Origin\nx-request-id: r-1\n");
		text.Should().NotContain("secret", "credential headers are never written, even when chosen");
	}

	[Test]
	public void ContentEncodingIsWrittenOnlyWhenChosen()
	{
		var section = Section("GET /", headers: [("Content-Encoding", "gzip")]);
		Render(section).Should().NotContain("content-encoding");
		Render(HttpResponseText.Chosen(["content-encoding"]), section).Should().Contain("content-encoding: gzip\n");
	}

	[Test]
	public void ReorderedJsonKeysMatch()
	{
		var verified = Render(Section("GET /", body: /*lang=json,strict*/ """{"a":1,"b":{"c":true,"d":[1,2]}}"""));
		var actual = Render(Section("GET /", body: /*lang=json,strict*/ """{"b":{"d":[1,2],"c":true},"a":1.0}"""));

		HttpSnapshot.Format.Compare(verified, actual).IsMatch.Should().BeTrue();
	}

	[Test]
	public void HeaderOrderIsIrrelevant()
	{
		const string verified = "## GET /\nstatus: 200 OK\ncontent-type: text/plain\nlocation: /a\n";
		const string actual = "## GET /\nstatus: 200 OK\nlocation: /a\ncontent-type: text/plain\n";

		HttpSnapshot.Format.Compare(verified, actual).IsMatch.Should().BeTrue();
	}

	[Test]
	public void AChangedFieldReportsItsPath()
	{
		var verified = Render(Login, Orders);
		var actual = Render(Login with { Body = Encoding.UTF8.GetBytes(/*lang=json,strict*/ """{"token":"xyz","user":{"name":"ada"}}""") }, Orders);

		var comparison = HttpSnapshot.Format.Compare(verified, actual);

		comparison.Differences.Should().ContainSingle().Which.Single().Should().StartWith("$[0].body.token");
		SnapshotMessages.Describe(comparison).Should().MatchSnapshot();
	}

	[Test]
	public void AnInsertedRequestIsOneDifference()
	{
		var verified = Render(Login, Orders);
		var actual = Render(Login, Section("GET {{host}}/api/whoami", body: /*lang=json,strict*/ """{"user":"ada"}"""), Orders);

		var comparison = HttpSnapshot.Format.Compare(verified, actual);

		comparison.Differences.Should().HaveCount(1);
		SnapshotMessages.Describe(comparison).Should().MatchSnapshot();
	}

	[Test]
	public void AChangedStatusIsOneDifference()
	{
		var comparison = HttpSnapshot.Format.Compare(Render(Orders), Render(Orders with { StatusCode = 500, ReasonPhrase = "Internal Server Error" }));

		comparison
			.Differences
			.Should()
			.ContainSingle()
			.Which
			.Single()
			.Should()
			.Be("$[0].status   expected \"404 Not Found\"   actual \"500 Internal Server Error\"   (verified.responses:2:9)");
	}

	[Test]
	public void TextBodiesAreQuotedSoTheyCannotStartASection()
	{
		var text = Render(Section("GET /text", body: "one\n## GET /evil\nstatus: 200 OK\n\n> quoted", mediaType: "text/plain"));

		text.Should().Be("## GET /text\nstatus: 200 OK\n\n> one\n> ## GET /evil\n> status: 200 OK\n>\n> > quoted\n");
		var section = (MapNode)HttpSnapshot.Format.Parse(text).As<SeqNode>().Items.Single();
		Field(section, "text").Should().Be("one\n## GET /evil\nstatus: 200 OK\n\n> quoted");
	}

	[Test]
	public void BinaryBodiesAreTheirSizeAndType()
	{
		var section = Section("GET /logo.png", body: null, mediaType: "image/png", charSet: null) with { Body = [0x89, 0x50, 0x4E, 0x47, 0x00] };

		var text = Render(section);

		text.Should().Be("## GET /logo.png\nstatus: 200 OK\n\nbinary: 5 bytes, image/png\n");
		Field((MapNode)HttpSnapshot.Format.Parse(text).As<SeqNode>().Items.Single(), "binary").Should().Be("5 bytes, image/png");
	}

	[Test]
	[Arguments("application/json", null, "body")]
	[Arguments("application/problem+json", null, "body")]
	[Arguments("text/html", null, "text")]
	[Arguments("application/xml", null, "text")]
	[Arguments("application/javascript", null, "text")]
	[Arguments("application/x-www-form-urlencoded", null, "text")]
	[Arguments("application/x-ndjson", null, "text")]
	[Arguments("application/octet-stream", "utf-8", "text")]
	[Arguments("application/octet-stream", null, "binary")]
	[Arguments(null, null, "binary")]
	public void BodiesAreClassifiedByMediaType(string? mediaType, string? charSet, string key)
	{
		var text = Render(Section("GET /", body: /*lang=json,strict*/ """{"a":1}""", mediaType: mediaType, charSet: charSet));

		((MapNode)HttpSnapshot.Format.Parse(text).As<SeqNode>().Items.Single()).Entries[^1].Key.Canonical.Should().Be(key);
	}

	[Test]
	public void InvalidJsonFallsBackToQuotedText()
	{
		var text = Render(Section("GET /", body: "{not json"));

		text.Should().Be("## GET /\nstatus: 200 OK\n\n> {not json\n");
	}

	[Test]
	public void JsonPositionsPointIntoTheWholeFile()
	{
		var tree = HttpSnapshot.Format.Parse(Render(Orders, Login)).As<SeqNode>();
		var body = (MapNode)((MapNode)tree.Items[1]).Entries.Single(e => e.Key.Canonical == "body").Value;

		body.Entries.Single(e => e.Key.Canonical == "token").Value.Span.Should().Be(new SourceSpan(9, 12));
	}

	[Test]
	[Arguments("status: 200", "Line 1: a section starts with '## ' and the request.")]
	[Arguments("## GET /\ncontent-type: x", "Line 2: the section's second line is 'status: ' and the status code.")]
	[Arguments("## GET /\nstatus: 200\nno colon", "Line 3: a header line is 'name: value'.")]
	[Arguments("## GET /\nstatus: 200\na: 1\na: 2", "Line 4: the header 'a' appears twice in one section.")]
	[Arguments("## GET /\nstatus: 200\n\n> a\nb", "Line 5: every line of a text body starts with '> '.")]
	[Arguments("## GET /\nstatus: 200\n\nbinary: 1 bytes\nx", "Line 5: a binary body is one line, 'binary: {n} bytes, {media type}'.")]
	public void RejectsWhatIsNotResponses(string text, string message) =>
		FluentActions.Invoking(() => HttpSnapshot.Format.Parse(text)).Should().Throw<FormatException>().WithMessage(message);

	[Test]
	public void ABodyThatIsNeitherJsonNorQuotedIsAFormatError() =>
		FluentActions
			.Invoking(() => HttpSnapshot.Format.Parse("## GET /\nstatus: 200\n\nhello"))
			.Should()
			.Throw<FormatException>()
			.Which
			.Message
			.Should()
			.StartWith("Line 4: the body is not valid JSON, quoted text ('> ') or 'binary: '.");

	[Test]
	public void AnInvalidVerifiedFileIsADifference() =>
		HttpSnapshot
			.Format
			.Compare("garbage", Render(Orders))
			.Differences
			.Single()
			.Single()
			.Should()
			.Be("The verified file is not valid HTTP: Line 1: a section starts with '## ' and the request.");

	private static readonly Gen<string> Words = Gen.String[Gen.Char["ab #>:{}\"-\n"], 0, 12];

	private static readonly Gen<HttpResponseSection> Sections = Gen.Select(
		Gen.OneOf(Gen.Const("GET {{host}}/a"), Gen.Const("login: POST /api/login HTTP/1.1"), Gen.String[Gen.Char["ab /#:"], 1, 10]),
		Gen.Int[100, 599],
		Gen.Int[0, 2].Select(
			i =>
				i switch
				{
					0 => null,
					1 => "OK",
					_ => "Not Found"
				}
		),
		Gen.Select(
			Gen.OneOf(Gen.Const("Content-Type"), Gen.Const("Location"), Gen.Const("X-Id"), Gen.Const("Vary")),
			Gen.String[Gen.Char["ab ,;="], 0, 6]
		).Array[0, 4],
		Gen.Int[0, 3],
		Words
	).Select(t =>
	{
		var (heading, status, reason, headers, kind, words) = t;
		(string body, string mediaType) = kind switch
		{
			0 => (string.Empty, "text/plain"),
			1 => (words, "text/plain"),
			2 => (System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["w"] = words, ["n"] = "1" }), "application/json"),
			_ => (words, "application/octet-stream"),
		};
		return new HttpResponseSection(
			heading.Trim().Replace("\n", " ", StringComparison.Ordinal) is { Length: > 0 } h ? h : "GET /",
			status,
			reason,
			[.. headers.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2))],
			Encoding.UTF8.GetBytes(body),
			mediaType,
			kind == 3 ? null : "utf-8"
		);
	});

	[Test]
	public void RenderThenParseGivesEverySection() =>
		Sections.Array[0, 4].Sample(sections =>
		{
			var chosen = HttpResponseText.Chosen(["x-id", "vary"]);
			var parsed = HttpSnapshot.Format.Parse(Render(chosen, sections)).As<SeqNode>().Items.Cast<MapNode>().ToArray();

			parsed.Should().HaveCount(sections.Length);
			for (var i = 0; i < sections.Length; i++)
			{
				var s = sections[i];
				var expected = new List<KeyValuePair<ScalarNode, Node>>
				{
					new(ScalarNode.String("request"), ScalarNode.String(s.Heading)),
					new(ScalarNode.String("status"), ScalarNode.String(s.ReasonPhrase is null ? $"{s.StatusCode}" : $"{s.StatusCode} {s.ReasonPhrase}")),
				};
				var headers = s
					.Headers
					.GroupBy(h => h.Key.ToLowerInvariant())
					.Select(
						g => new KeyValuePair<ScalarNode, Node>(ScalarNode.String(g.Key), ScalarNode.String(string.Join(", ", g.Select(h => h.Value.Trim()))))
					)
					.ToList();
				if (headers.Count > 0)
					expected.Add(new(ScalarNode.String("headers"), new MapNode(headers)));
				var body = Encoding.UTF8.GetString(s.Body);
				if (body.Length > 0)
				{
					expected.Add(s.MediaType switch
					{
						"application/json" => new(ScalarNode.String("body"), JsonTree.Parse(body)),
						"text/plain" => new(ScalarNode.String("text"), ScalarNode.String(body)),
						_ => new(ScalarNode.String("binary"), ScalarNode.String($"{s.Body.Length} bytes, {s.MediaType}")),
					});
				}

				TreeDiffer.Instance.Diff(new MapNode(expected), parsed[i]).Should().BeEmpty();
			}
		});

	[Test]
	public void RenderedTextComparesEqualToItself() =>
		Sections.Array[0, 4].Sample(sections =>
		{
			var text = Render(sections);
			HttpSnapshot.Format.Compare(text, text).IsMatch.Should().BeTrue();
		});

	private static string Field(MapNode section, string key) => ((ScalarNode)section.Entries.Single(e => e.Key.Canonical == key).Value).Canonical;
}
