using Nullean.Snapshots.TUnit;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>The project's journeys, a test case each, except the basket ones, which <see cref="BasketJourneys"/> claims.</summary>
public class ProjectJourneys
{
	[Before(Test)]
	public void RequireChrome() => LiveJourneyTests.RequireChrome();

	[Test]
	[JourneyFiles("journeys")]
	public Task Run(SnapshotFile journey)
	{
		var options = new JourneySnapshotOptions { BaseUrl = LiveJourneyTests.Site };
		if (JourneyTest.SkipReason(journey, options) is { } why)
			Skip.Test(why);
		return JourneyTest.RunAsync(journey, options);
	}
}

/// <summary>
/// Journeys that need a server: telemetry-journeys/ against <see cref="TelemetryServer"/>, a page that posts to a collector.
/// Not under journeys/, whose base URL is the file:// site.
/// </summary>
public sealed class TelemetryJourneys : IAsyncDisposable
{
	private TelemetryServer? _server;

	[Before(Test)]
	public void StartServer()
	{
		LiveJourneyTests.RequireChrome();
		_server = new TelemetryServer();
	}

	public async ValueTask DisposeAsync()
	{
		if (_server is not null)
			await _server.DisposeAsync();
	}

	[Test]
	[JourneyFiles("telemetry-journeys")]
	public Task Run(SnapshotFile journey) => JourneyTest.RunAsync(journey, new JourneySnapshotOptions { BaseUrl = new Uri(_server!.Url) });
}

/// <summary>A more specific folder with its own class, as a folder needing different setup would have.</summary>
public class BasketJourneys
{
	[Before(Test)]
	public void RequireChrome() => LiveJourneyTests.RequireChrome();

	[Test]
	[JourneyFiles("journeys/basket")]
	public Task Run(SnapshotFile journey) => JourneyTest.RunAsync(journey, new JourneySnapshotOptions { BaseUrl = LiveJourneyTests.Site });
}
