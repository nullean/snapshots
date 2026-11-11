using System.Text;
using AwesomeAssertions;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>Shape mode: a response written as its properties and their kinds, never their values.</summary>
public class HttpShapeTests
{
	private static readonly IReadOnlySet<string> Defaults = HttpResponseText.Chosen(null);

	private static string Render(string? body, string? mediaType = "application/json", int status = 200, bool shape = true)
	{
		var text = new StringBuilder();
		HttpResponseText.Append(text, new HttpResponseSection("GET {{host}}/api/orders", status, status == 200 ? "OK" : "Not Found", [],
			body is null ? [] : Encoding.UTF8.GetBytes(body), mediaType, mediaType is null or "application/octet-stream" ? null : "utf-8"), Defaults, shape);
		return text.ToString();
	}

	[Test]
	public void AJsonBodyIsWrittenAsItsShape() =>
		Render(/*lang=json,strict*/ """{"total":42,"price":9.5,"open":true,"customer":{"name":"ada","vip":null},"lines":[{"sku":"a","qty":1},{"sku":"b","qty":2,"note":"x"}]}""")
			.Should().Be("""
				## GET {{host}}/api/orders
				status: 200 OK

				{
				  "customer": {
				    "name": "string",
				    "vip": null
				  },
				  "lines": [
				    {
				      "note": "string",
				      "qty": 1,
				      "sku": "string"
				    }
				  ],
				  "open": false,
				  "price": 1.0,
				  "total": 1
				}

				""".Replace("\r\n", "\n", StringComparison.Ordinal));

	[Test]
	public void ATextBodyIsItsMediaTypeAlone() =>
		Render("<html>changes every minute</html>", "text/html")
			.Should().Be("## GET {{host}}/api/orders\nstatus: 200 OK\n\ntext: text/html\n");

	[Test]
	public void InvalidJsonWithAJsonTypeIsTextSoItsShapeIsItsType() =>
		Render("{not json", "application/json").Should().EndWith("\ntext: application/json\n");

	[Test]
	public void ABinaryBodyIsUnchanged() =>
		Render("abc", "application/octet-stream").Should().Be(Render("abc", "application/octet-stream", shape: false));

	[Test]
	public void ShapeTextParsesAndRoundTrips()
	{
		var text = Render("hello", "text/plain");

		var section = (MapNode)HttpSnapshot.Format.Parse(text).As<SeqNode>().Items.Single();

		section.Entries.Single(e => e.Key.Canonical == "text-type").Value.Should().BeOfType<ScalarNode>().Which.Canonical.Should().Be("text/plain");
	}

	[Test]
	public void ChangedValuesAndADifferentNumberOfItemsMatch()
	{
		var verified = Render(/*lang=json,strict*/ """{"id":1,"hits":[{"a":"x"},{"a":"y"}]}""");
		var actual = Render(/*lang=json,strict*/ """{"hits":[{"a":"z"}],"id":99}""");

		HttpSnapshot.Format.Compare(verified, actual).IsMatch.Should().BeTrue();
	}

	[Test]
	public void AnAddedPropertyIsOneDifference()
	{
		var verified = Render(/*lang=json,strict*/ """{"id":1,"hits":[{"a":"x"}]}""");
		var actual = Render(/*lang=json,strict*/ """{"id":1,"hits":[{"a":"x","b":"y"}]}""");

		var comparison = HttpSnapshot.Format.Compare(verified, actual);

		comparison.Differences.Should().ContainSingle().Which.Single().Should().StartWith("$[0].body.hits[0].b");
	}

	[Test]
	public void ADroppedPropertyAndAChangedKindAreEachOneDifference()
	{
		var verified = Render(/*lang=json,strict*/ """{"id":1,"name":"a","n":1}""");
		var actual = Render(/*lang=json,strict*/ """{"id":"1","n":1}""");

		HttpSnapshot.Format.Compare(verified, actual).Differences.Should().HaveCount(2);
	}

	[Test]
	public void ATextBodyWhoseMediaTypeChangesIsADifference() =>
		HttpSnapshot.Format.Compare(Render("a", "text/html"), Render("a", "text/plain")).IsMatch.Should().BeFalse();

	[Test]
	public void ThePlainRenderingIsUnchangedWhenShapeIsOff() =>
		Render(/*lang=json,strict*/ """{"id":1}""", shape: false).Should().Contain("\"id\": 1");
}
