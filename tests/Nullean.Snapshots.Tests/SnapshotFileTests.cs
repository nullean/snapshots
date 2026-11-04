using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;

namespace Nullean.Snapshots.Tests;

public class SnapshotFileTests
{
	private static readonly string Project = MockUnixSupport.Path(@"c:\repo\tests\Shop.Tests");

	private static MockFileSystem Files(params string[] paths)
	{
		var fs = new MockFileSystem();
		foreach (var path in paths)
			fs.AddFile(fs.Path.Combine([Project, .. path.Split('/')]), new("x"));
		return fs;
	}

	[Test]
	public void FindsMatchingFilesSortedWithNamesBelowTheFolder()
	{
		var fs = Files("journeys/shop.journey", "journeys/basket/empty.journey", "journeys/notes.txt");

		SnapshotFile.Find(fs, Project, "journeys", "*.journey").Should().Equal(
			new SnapshotFile("journeys/basket/empty.journey", "basket/empty"),
			new SnapshotFile("journeys/shop.journey", "shop"));
	}

	[Test]
	public void SkipsSnapshotsBuildOutputAndDotFolders()
	{
		var fs = Files("journeys/shop.journey", "journeys/.snapshots/shop.verified.journey", "journeys/bin/a.journey",
			"journeys/obj/b.journey", "journeys/node_modules/c.journey", "journeys/.hidden/d.journey");

		SnapshotFile.Find(fs, Project, "journeys", "*.journey").Select(f => f.Name).Should().Equal("shop");
	}

	[Test]
	public void AMissingFolderHasNoFiles() =>
		SnapshotFile.Find(Files("journeys/shop.journey"), Project, "elsewhere", "*.journey").Should().BeEmpty();

	[Test]
	public void ShowsAsItsName() => new SnapshotFile("journeys/basket/empty.journey", "basket/empty").ToString().Should().Be("basket/empty");
}
