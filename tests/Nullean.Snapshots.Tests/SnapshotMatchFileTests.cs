using AwesomeAssertions;

namespace Nullean.Snapshots.Tests;

public class SnapshotMatchFileTests
{
	private static string Journey(TestProject p, params string[] parts) => p.Fs.Path.Combine([p.Project.Directory, .. parts]);

	[Test]
	public void KeyIsTheFilesPathInTheProjectWithoutItsExtension()
	{
		var p = new TestProject();
		var result = p.MatchFile("a\n", Journey(p, "journeys", "checkout.journey"));

		result.Passed.Should().BeFalse();
		result.Key.Should().Be("Acme.Tests/journeys/checkout");
		result.VerifiedPath.Should().Be(p.VerifiedFile("journeys/checkout"));
		p.Read(p.ReceivedFile("journeys/checkout")).Should().Be("a\n");
	}

	[Test]
	public void FileAtTheProjectRootHasNoDirectorySegment()
	{
		var p = new TestProject();
		p.MatchFile("a", Journey(p, "landing.journey")).Key.Should().Be("Acme.Tests/landing");
	}

	[Test]
	public void RelativePathResolvesAgainstTheProjectDirectory()
	{
		var p = new TestProject();
		p.MatchFile("a", p.Fs.Path.Combine("journeys", "checkout.journey")).Key.Should().Be("Acme.Tests/journeys/checkout");
	}

	[Test]
	public void KeyDoesNotDependOnTheCallingTest()
	{
		var fromTest = new TestProject { Identity = TestProject.TUnitLike("JourneyTests", "Runs", "checkout.journey") };
		var fromCli = new TestProject();

		var a = fromTest.MatchFile("a", Journey(fromTest, "journeys", "checkout.journey"));
		var b = fromCli.MatchFile("a", Journey(fromCli, "journeys", "checkout.journey"));

		a.Key.Should().Be(b.Key);
		a.VerifiedPath.Should().Be(b.VerifiedPath);
	}

	[Test]
	public void MatchesAndRemovesTheReceivedFile()
	{
		var p = new TestProject();
		p.AddVerified("journeys/checkout", "same\r\n");
		p.Fs.AddFile(p.ReceivedFile("journeys/checkout"), new("old"));

		p.MatchFile("same", Journey(p, "journeys", "checkout.journey")).Passed.Should().BeTrue();
		p.Fs.File.Exists(p.ReceivedFile("journeys/checkout")).Should().BeFalse();
	}

	[Test]
	public void FailureMessageHasNoTestFilterWithoutATest()
	{
		var p = new TestProject();
		p.AddVerified("journeys/checkout", "a\n");

		var message = p.MatchFile("b", Journey(p, "journeys", "checkout.journey")).FailureMessage;

		message.Should().Contain("Accept:   snap accept Acme.Tests/journeys/checkout");
		message.Should().NotContain("--treenode-filter");
	}

	[Test]
	public void FailureMessageCarriesTheCallingTestsFilter()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike("JourneyTests", "Runs") };
		p
			.MatchFile("b", Journey(p, "journeys", "checkout.journey"))
			.FailureMessage
			.Should()
			.Contain("--treenode-filter \"/Acme.Tests/Acme.Tests.Rendering/JourneyTests/Runs\"");
	}

	[Test]
	public void AcceptWritesTheVerifiedFile()
	{
		var p = new TestProject();
		p.Environment["SNAPSHOT_ACCEPT"] = "1";

		p.MatchFile("new", Journey(p, "journeys", "checkout.journey")).Passed.Should().BeTrue();
		p.Read(p.VerifiedFile("journeys/checkout")).Should().Be("new\n");
	}

	[Test]
	public void IsRecordedInTheRunManifest()
	{
		var p = new TestProject();
		p.MatchFile("a", Journey(p, "journeys", "checkout.journey"));
		p.Engine.FlushManifests();

		var (files, _) = RunManifest.Read(p.Read(p.Fs.Path.Combine(p.Project.ReceivedDirectory, RunManifest.FileName)));
		files.Should().BeEquivalentTo(["journeys/checkout.verified.txt"]);
	}

	[Test]
	public void TwoTestsRunningOneFileFail()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike("A", "Runs") };
		var file = Journey(p, "journeys", "checkout.journey");
		p.MatchFile("a", file);
		p.Identity = TestProject.TUnitLike("B", "Runs");

		var second = () => p.MatchFile("a", file);

		second.Should().Throw<InvalidOperationException>().WithMessage("Two snapshot assertions resolve to Acme.Tests/journeys/checkout*");
	}

	[Test]
	public void TheSameTestAgainIsFine()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike("A", "Runs") };
		var file = Journey(p, "journeys", "checkout.journey");
		p.MatchFile("a", file);

		p.MatchFile("a", file).Passed.Should().BeFalse("it is still a new snapshot, but not a duplicate");
	}

	[Test]
	public void FileOutsideTheProjectIsRejected()
	{
		var p = new TestProject();
		var outside = () => p.MatchFile("a", p.Fs.Path.Combine(TestProject.Root, "elsewhere", "x.journey"));
		var escaping = () => p.MatchFile("a", p.Fs.Path.Combine("..", "x.journey"));

		outside.Should().Throw<ArgumentException>().WithMessage("*is not inside the test project*");
		escaping.Should().Throw<ArgumentException>().WithMessage("*is not inside the test project*");
	}

	[Test]
	public void FileWithoutANameIsRejected()
	{
		var p = new TestProject();
		var act = () => p.MatchFile("a", Journey(p, "journeys", ".journey"));
		act.Should().Throw<ArgumentException>().WithMessage("*no file name*");
	}
}
