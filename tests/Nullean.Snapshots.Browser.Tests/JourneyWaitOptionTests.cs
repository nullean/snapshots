using AwesomeAssertions;
using Nullean.Cdp;
using Nullean.Snapshots.Cli;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>How <c>--wait-for</c> and its options become preconditions.</summary>
public class JourneyWaitOptionTests
{
	private static (bool Ok, List<Cdp.Journeys.JourneyPrecondition> Preconditions, string Problem) Parse(JourneyRunArgs args)
	{
		var ok = JourneyCli.TryParseWaits(args, out var preconditions, out var problem);
		return (ok, preconditions, problem);
	}

	[Test]
	public void AHeaderAnAttributeAndTheTextOfAnElementAreRead()
	{
		var (ok, preconditions, _) = Parse(new JourneyRunArgs
		{
			WaitFor =
			[
				"header:x-docs-build==1.74.1",
				"meta[name='docs:build']@content==1.74.1",
				"h1#build==Ready",
				"a[href*='x']@href=*/docs",
				@"header:Server=~^Amazon"
			],
		});

		ok.Should().BeTrue();
		preconditions
			.Select(p => (p.Header, p.Selector, p.Attribute, p.Expected, p.Match))
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
	public void TheOptionsApplyToEveryWaitWithTheirDefaults()
	{
		var defaults = Parse(new JourneyRunArgs { WaitFor = ["header:x==1"] }).Preconditions.Single();
		var given = Parse(new JourneyRunArgs
		{
			WaitFor = ["header:x==1", "header:y==2"],
			WaitPath = "/docs",
			WaitTimeout = "90s",
			WaitInterval = "250ms",
			WaitRetries = 0
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
		JourneyCli.TryParseDuration("--wait-timeout", value, TimeSpan.Zero, out var duration, out _).Should().BeTrue();

		duration.TotalSeconds.Should().Be(seconds);
	}

	[Test]
	[Arguments("nonsense")]
	[Arguments("header:x")]
	[Arguments("==1")]
	[Arguments("header:==1")]
	[Arguments("h1@==1")]
	[Arguments("h1==")]
	public void AWaitThatIsNotOneIsAUsageError(string value)
	{
		var (ok, _, problem) = Parse(new JourneyRunArgs { WaitFor = [value] });

		ok.Should().BeFalse();
		problem.Should().StartWith("Error: --wait-for '" + value + "'");
	}

	[Test]
	public void ABadDurationOrRetriesOrAnOptionWithoutAWaitIsAUsageError()
	{
		Parse(new JourneyRunArgs { WaitFor = ["header:x==1"], WaitTimeout = "soon" })
			.Problem
			.Should()
			.Be("Error: --wait-timeout 'soon' is not a duration: use 10s, 500ms or 2m.");
		Parse(new JourneyRunArgs { WaitFor = ["header:x==1"], WaitRetries = -1 }).Problem.Should().Be("Error: --wait-retries -1 is not zero or more.");
		Parse(new JourneyRunArgs { WaitTimeout = "5s" }).Problem.Should().StartWith("Error: --wait-path, --wait-timeout");
		Parse(new JourneyRunArgs()).Ok.Should().BeTrue();
	}
}
