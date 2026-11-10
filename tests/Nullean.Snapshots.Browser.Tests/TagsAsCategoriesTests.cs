using AwesomeAssertions;
using Nullean.Snapshots.TUnit;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>
/// <c>[JourneyFiles]</c> gives each case the journey's <c># @tags</c> as TUnit categories, so
/// <c>--treenode-filter "/*/*/*/*[Category=smoke]"</c> selects by tag.
/// </summary>
public class TagsAsCategoriesTests
{
	[Test]
	[JourneyFiles("journeys")]
	public void Case_Categories_AreTheJourneysTags(SnapshotFile journey)
	{
		var categories = TestContext.Current!.Metadata.TestDetails.Categories;

		categories.Should().BeEquivalentTo(journey.Tags(typeof(TagsAsCategoriesTests).Assembly));
		if (journey.Name == "shop")
			categories.Should().BeEquivalentTo(["shop", "smoke"]);
	}

	[Test]
	[JourneyFiles("journeys")]
	public void Case_Argument_IsTheFileAndNotTheRowThatCarriesTheCategories(SnapshotFile journey) =>
		TestContext.Current!.Metadata.TestDetails.TestMethodArguments.Should().ContainSingle().Which.Should().BeSameAs(journey);
}
