using AwesomeAssertions;

namespace Nullean.Snapshots.Tests;

public class SnapshotArtifactsTests
{
	private static string Journey(TestProject p, params string[] parts) => p.Fs.Path.Combine([p.Project.Directory, .. parts]);

	[Test]
	public void IsAFolderNextToTheReceivedSnapshot()
	{
		var p = new TestProject();

		var folder = p.Engine.ArtifactsDirectory(Journey(p, "journeys", "checkout.journey"));

		folder.Should().Be(p.Fs.Path.Combine(p.Project.ReceivedDirectory, "journeys", "checkout.artifacts"));
		p.Fs.Directory.Exists(folder).Should().BeTrue();
	}

	[Test]
	public void AtTheProjectRootItHasNoFolderSegment()
	{
		var p = new TestProject();

		p.Engine.ArtifactsDirectory(Journey(p, "landing.journey")).Should().Be(p.Fs.Path.Combine(p.Project.ReceivedDirectory, "landing.artifacts"));
	}

	[Test]
	public void IsEmptiedSoAnEarlierRunLeavesNothing()
	{
		var p = new TestProject();
		var stale = p.Fs.Path.Combine(p.Project.ReceivedDirectory, "journeys", "checkout.artifacts", "shots", "old.png");
		p.Fs.AddFile(stale, new("png"));
		var neighbour = p.ReceivedFile("journeys/checkout", "journey");
		p.Fs.AddFile(neighbour, new("received"));

		var folder = p.Engine.ArtifactsDirectory(Journey(p, "journeys", "checkout.journey"));

		p.Fs.Directory.EnumerateFileSystemEntries(folder).Should().BeEmpty();
		p.Fs.File.Exists(neighbour).Should().BeTrue("only the artifacts folder is emptied");
	}

	[Test]
	public void AFileOutsideTheProjectIsRefused()
	{
		var p = new TestProject();
		var outside = p.Fs.Path.Combine(p.Fs.Path.GetDirectoryName(p.Project.Directory)!, "elsewhere.journey");

		var act = () => p.Engine.ArtifactsDirectory(outside);

		act.Should().Throw<ArgumentException>().WithMessage("*is not inside the test project*");
	}
}
