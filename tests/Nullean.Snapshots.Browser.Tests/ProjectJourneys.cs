using Nullean.Snapshots.TUnit;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>The project's journeys, a test case each, except the basket ones, which <see cref="BasketJourneys"/> claims.</summary>
public class ProjectJourneys
{
	[Before(Test)]
	public void RequireChrome() => LiveJourneyTests.RequireChrome();

	[Test]
	[JourneyFiles("journeys")]
	[NotInParallel(LiveJourneyTests.ArtifactsFolder)]
	public Task Run(SnapshotFile journey) => JourneyTest.RunAsync(journey, new JourneySnapshotOptions { BaseUrl = LiveJourneyTests.Site });
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
