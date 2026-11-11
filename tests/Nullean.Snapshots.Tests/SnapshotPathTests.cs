using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;

namespace Nullean.Snapshots.Tests;

public class SnapshotPathTests
{
	private static readonly int[] OneTwo = [1, 2];

	private static readonly MockFileSystem Fs = new();
	private static string P(params string[] parts) => Fs.Path.Combine([TestProject.Root, .. parts]);

	[Test]
	[Arguments("/_/tests/Acme.Tests/Rendering/HtmlTests.cs", "Rendering")]
	[Arguments("/_/tests/Acme.Tests/HtmlTests.cs", "")]
	[Arguments("/_1/tests/Acme.Tests/A/B/HtmlTests.cs", "A/B")]
	[Arguments("/_/Acme.Tests/Rendering/HtmlTests.cs", "Rendering")]
	public void DeterministicPathsMapBackToTheProject(string callerFilePath, string expected) =>
		SnapshotPath.RelativeTestDirectory(Fs, P("tests", "Acme.Tests"), callerFilePath).Should().Be(expected);

	[Test]
	public void RealPathIsRelativeToTheProject() =>
		SnapshotPath.RelativeTestDirectory(Fs, P("tests", "Acme.Tests"), P("tests", "Acme.Tests", "Rendering", "Html", "X.cs")).Should().Be("Rendering/Html");

	[Test]
	public void FileOutsideTheProjectGoesToTheRoot() =>
		SnapshotPath.RelativeTestDirectory(Fs, P("tests", "Acme.Tests"), P("shared", "X.cs")).Should().BeEmpty();

	[Test]
	[Arguments("/_/tests/X.cs", true)]
	[Arguments("/_12/X.cs", true)]
	[Arguments("/_x/X.cs", false)]
	[Arguments("/home/_/X.cs", false)]
	public void RecognisesDeterministicPaths(string path, bool expected) => SnapshotPath.IsDeterministic(path).Should().Be(expected);

	[Test]
	public void KeyLeavesOutEmptyParts()
	{
		SnapshotPath.Key("Acme.Tests", "", "HtmlTests", "RendersTable", null, null).Should().Be("Acme.Tests/HtmlTests/RendersTable");
		SnapshotPath
			.Key("Acme.Tests", "Rendering", "HtmlTests", "RendersPage", "header", "a1b2c3d4")
			.Should()
			.Be("Acme.Tests/Rendering/HtmlTests/RendersPage.header.a1b2c3d4");
	}

	[Test]
	[Arguments("Generic`1", "Generic")]
	[Arguments("Outer+Inner", "Outer_Inner")]
	[Arguments("a b/c:d", "a_b_c_d")]
	public void Sanitizes(string input, string expected) => SnapshotPath.Sanitize(input).Should().Be(expected);

	[Test]
	[Arguments("header", true)]
	[Arguments("with-dash_and.dot", true)]
	[Arguments("", false)]
	[Arguments(".hidden", false)]
	[Arguments("trailing.", false)]
	[Arguments("a/b", false)]
	public void ValidatesNames(string name, bool expected) => SnapshotPath.IsValidName(name).Should().Be(expected);

	// The hash is part of file names, so it must never change between versions, machines or cultures.
	[Test]
	public void ArgumentsHashIsStable()
	{
		SnapshotPath.ArgumentsHash([]).Should().BeEmpty();
		SnapshotPath.ArgumentsHash([1, "a", null, 1.5]).Should().Be("c51fda38");
		SnapshotPath.ArgumentsHash([OneTwo]).Should().Be(SnapshotPath.ArgumentsHash([new List<int> { 1, 2 }]));
		SnapshotPath.ArgumentsHash(["1"]).Should().NotBe(SnapshotPath.ArgumentsHash([1]));
	}

	[Test]
	public void VerifiedAndReceivedSwapByName() => SnapshotPath.ToVerifiedName("RendersTable.received.html").Should().Be("RendersTable.verified.html");

	[Test]
	public void MarkerRoundTrips()
	{
		var marker = new ReceivedMarker("Acme.Tests", "../../../../tests/Acme.Tests/.snapshots");
		ReceivedMarker.Parse(marker.Render()).Should().Be(marker);
		ReceivedMarker.Parse("verified=x\n").Should().BeNull();
	}

	[Test]
	public void MarkerPointsFromTheReceivedFolderToTheVerifiedFolder()
	{
		var project = new TestProject().Project;
		IFileSystem fs = Fs;
		var marker = ReceivedMarker.For(fs, project);
		marker.VerifiedDirectory.Should().Be("../../../../tests/Acme.Tests/.snapshots");
		marker.ResolveVerified(fs, project.ReceivedDirectory).Should().Be(project.SnapshotsDirectory);
	}
}
