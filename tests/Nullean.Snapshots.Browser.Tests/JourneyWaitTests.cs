using AwesomeAssertions;
using Nullean.Cdp;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Browser.Console;
using Nullean.Snapshots.Cli;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>How the wait flags of a command line become preconditions.</summary>
public class JourneyWaitTests
{
	private static JourneyWaitParse Parse(params string[] checks) => JourneyWait.Parse(new JourneyWaitFlags { Checks = checks });

	private static (string? Header, string? Selector, string? Attribute, string Expected, TextMatch Match) Shape(JourneyPrecondition p) =>
		(p.Header, p.Selector, p.Attribute, p.Expected, p.Match);

	[Test]
	public void AHeaderAnAttributeAndTheTextOfAnElementAreRead()
	{
		var parsed = Parse(
			"header:x-docs-build==1.74.1",
			"meta[name='docs:build']@content==1.74.1",
			"h1#build==Ready",
			"a[href*='x']@href=*/docs",
			@"header:Server=~^Amazon"
		);

		parsed.Valid.Should().BeTrue();
		parsed
			.Preconditions
			.Select(Shape)
			.Should()
			.Equal(
				("x-docs-build", null, null, "1.74.1", TextMatch.Exact),
				(null, "meta[name='docs:build']", "content", "1.74.1", TextMatch.Exact),
				(null, "h1#build", null, "Ready", TextMatch.Exact),
				(null, "a[href*='x']", "href", "/docs", TextMatch.Contains),
				("Server", null, null, "^Amazon", TextMatch.Regex)
			);
	}

	[Test]
	public void AnAtSignOrAnOperatorInsideQuotesBelongsToTheSelector()
	{
		var parsed = Parse(
			"a[href='mailto:docs@elastic.co']==Contact",
			"a[href='mailto:docs@elastic.co']@href==mailto:docs@elastic.co",
			"a[title=\"a==b\"]==Link",
			"a[title='x=~y'][href]@title=*y",
			"p@lang==en@gb"
		);

		parsed.Valid.Should().BeTrue(parsed.Problem);
		parsed
			.Preconditions
			.Select(Shape)
			.Should()
			.Equal(
				(null, "a[href='mailto:docs@elastic.co']", null, "Contact", TextMatch.Exact),
				(null, "a[href='mailto:docs@elastic.co']", "href", "mailto:docs@elastic.co", TextMatch.Exact),
				(null, "a[title=\"a==b\"]", null, "Link", TextMatch.Exact),
				(null, "a[title='x=~y'][href]", "title", "y", TextMatch.Contains),
				(null, "p", "lang", "en@gb", TextMatch.Exact)
			);
	}

	[Test]
	public void TheOptionsApplyToEveryCheckWithTheirDefaults()
	{
		var defaults = Parse("header:x==1").Preconditions.Single();
		var given = JourneyWait.Parse(new JourneyWaitFlags
		{
			Checks = ["header:x==1", "header:y==2"],
			Path = "/docs",
			Timeout = "90s",
			Interval = "250ms",
			Retries = 0
		}).Preconditions;

		(defaults.Path, defaults.Timeout, defaults.Interval, defaults.Retries).Should().Be(("./", TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), 2));
		given.Should().OnlyContain(
			p => p.Path == "/docs" && p.Timeout == TimeSpan.FromSeconds(90) && p.Interval == TimeSpan.FromMilliseconds(250) && p.Retries == 0
		);
	}

	[Test]
	[Arguments("2m", 120.0)]
	[Arguments("1500ms", 1.5)]
	[Arguments("7", 7.0)]
	[Arguments("0.5s", 0.5)]
	public void DurationsTakeAUnitOrBareSeconds(string value, double seconds)
	{
		var parsed = JourneyWait.Parse(new JourneyWaitFlags { Checks = ["header:x==1"], Timeout = value });

		parsed.Preconditions.Single().Timeout.TotalSeconds.Should().Be(seconds);
	}

	[Test]
	[Arguments("nonsense")]
	[Arguments("header:x")]
	[Arguments("==1")]
	[Arguments("header:==1")]
	[Arguments("h1@==1")]
	[Arguments("h1==")]
	[Arguments("a[href='x==1']")]
	public void ACheckThatIsNotOneIsAProblem(string value)
	{
		var parsed = Parse(value);

		parsed.Valid.Should().BeFalse();
		parsed.Preconditions.Should().BeEmpty();
		parsed.Problem.Should().StartWith("--wait-for '" + value + "'");
	}

	[Test]
	public void ABadDurationOrRetriesOrOptionsWithoutACheckAreProblems()
	{
		JourneyWait
			.Parse(new JourneyWaitFlags { Checks = ["header:x==1"], Timeout = "soon" })
			.Problem
			.Should()
			.Be("--wait-timeout 'soon' is not a duration: use 10s, 500ms or 2m.");
		JourneyWait.Parse(new JourneyWaitFlags { Checks = ["header:x==1"], Retries = -1 }).Problem.Should().Be("--wait-retries -1 is not zero or more.");
		JourneyWait.Parse(new JourneyWaitFlags { Timeout = "5s" }).Problem.Should().StartWith("--wait-path, --wait-timeout");
		JourneyWait.Parse(new JourneyWaitFlags()).Should().Be(new JourneyWaitParse([]));
	}

	[Test]
	public void TheCliPrefixesAProblemAsAUsageError()
	{
		JourneyCli.TryParseWaits(new JourneyRunArgs { WaitFor = ["nonsense"] }, out var preconditions, out var problem).Should().BeFalse();

		preconditions.Should().BeEmpty();
		problem.Should().StartWith("Error: --wait-for 'nonsense' is not a check");
	}
}
