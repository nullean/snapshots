using System.IO.Abstractions;
using AwesomeAssertions;
using Nullean.ScopedFileSystem;

namespace Nullean.Snapshots.Tests;

public class SnapshotEngineTests
{
	private const string Key = "Rendering/HtmlTests/RendersTable";

	[Test]
	public void NewSnapshotFailsAndWritesReceived()
	{
		var p = new TestProject();
		var result = p.Match("hello\r\nworld");

		result.Passed.Should().BeFalse();
		result.Key.Should().Be("Acme.Tests/" + Key);
		p.Read(p.ReceivedFile(Key)).Should().Be("hello\nworld\n");
		p.Fs.File.Exists(p.VerifiedFile(Key)).Should().BeFalse();
		result
			.FailureMessage
			.Should()
			.StartWith("Text snapshot is new: no verified file at tests/Acme.Tests/.snapshots/Rendering/HtmlTests/RendersTable.verified.txt");
	}

	[Test]
	public void ReceivedFolderGetsAMarkerPointingAtTheVerifiedFolder()
	{
		var p = new TestProject();
		p.Match("x");
		var marker = ReceivedMarker.Parse(p.Read(p.Fs.Path.Combine(p.Project.ReceivedDirectory, ReceivedMarker.FileName)));
		marker.Should().NotBeNull();
		marker.Project.Should().Be("Acme.Tests");
		marker.ResolveVerified(p.Fs, p.Project.ReceivedDirectory).Should().Be(p.Project.SnapshotsDirectory);
	}

	[Test]
	public void MatchPassesAndRemovesAStaleReceivedFile()
	{
		var p = new TestProject();
		p.AddVerified(Key, "same\r\n");
		p.Fs.AddFile(p.ReceivedFile(Key), new("old"));

		var result = p.Match("same");

		result.Passed.Should().BeTrue();
		result.FailureMessage.Should().BeNull();
		p.Fs.File.Exists(p.ReceivedFile(Key)).Should().BeFalse();
	}

	[Test]
	public void PassingCompareNeverRewritesVerified()
	{
		var p = new TestProject();
		p.AddVerified(Key, "same\r\n");
		p.Match("same");
		p.Read(p.VerifiedFile(Key)).Should().Be("same\r\n");
	}

	[Test]
	public void MismatchFailsWritesReceivedAndKeepsVerified()
	{
		var p = new TestProject();
		p.AddVerified(Key, "a\nb\n");

		var result = p.Match("a\nB\n");

		result.Passed.Should().BeFalse();
		p.Read(p.VerifiedFile(Key)).Should().Be("a\nb\n");
		p.Read(p.ReceivedFile(Key)).Should().Be("a\nB\n");
	}

	[Test]
	public void AcceptWritesVerifiedAndPasses()
	{
		var p = new TestProject();
		p.Environment["SNAPSHOT_ACCEPT"] = "1";
		p.AddVerified(Key, "old\n");
		p.Fs.AddFile(p.ReceivedFile(Key), new("old received"));

		var result = p.Match("new");

		result.Passed.Should().BeTrue();
		p.Read(p.VerifiedFile(Key)).Should().Be("new\n");
		p.Fs.File.Exists(p.ReceivedFile(Key)).Should().BeFalse();
	}

	[Test]
	[Arguments("true")]
	[Arguments("1")]
	public void CiNeverAccepts(string ci)
	{
		var p = new TestProject();
		p.Environment["SNAPSHOT_ACCEPT"] = "1";
		p.Environment["CI"] = ci;

		var result = p.Match("new");

		result.Passed.Should().BeFalse();
		p.Fs.File.Exists(p.VerifiedFile(Key)).Should().BeFalse();
		result.FailureMessage.Should().EndWith("SNAPSHOT_ACCEPT is ignored because CI is set: snapshots are never accepted in CI.");
	}

	[Test]
	[Arguments("0")]
	[Arguments("false")]
	[Arguments("")]
	public void AcceptNeedsATrueValue(string value)
	{
		var p = new TestProject();
		p.Environment["SNAPSHOT_ACCEPT"] = value;
		p.Match("new").Passed.Should().BeFalse();
	}

	[Test]
	public void DifferentTestsWithTheSameKeyFail()
	{
		var p = new TestProject();
		p.Match("x", line: 10);
		var act = () => p.Match("x", line: 20);
		act
			.Should()
			.Throw<InvalidOperationException>()
			.WithMessage("Two snapshot assertions resolve to Acme.Tests/Rendering/HtmlTests/RendersTable; pass a name to tell them apart.");
	}

	[Test]
	public void SameCallSiteMayClaimItsKeyAgain()
	{
		var p = new TestProject();
		p.Match("x");
		var act = () => p.Match("x");
		act.Should().NotThrow();
	}

	[Test]
	public void NamesTellSnapshotsApart()
	{
		var p = new TestProject();
		p.Match("x", name: "first", line: 10).Key.Should().EndWith("RendersTable.first");
		p.Match("x", name: "second", line: 20).Key.Should().EndWith("RendersTable.second");
	}

	[Test]
	public void InvalidNamesAreRejected()
	{
		var p = new TestProject();
		var act = () => p.Match("x", name: "../escape");
		act.Should().Throw<ArgumentException>();
	}

	[Test]
	public void IdentityGivesTheRuntimeClassAndArgumentsHash()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike("DerivedHtmlTests", "RendersTable", 1, "a") };
		var result = p.Match("x");
		result.Key.Should().Be($"Acme.Tests/Rendering/DerivedHtmlTests/RendersTable.{SnapshotPath.ArgumentsHash([1, "a"])}");
		result
			.FailureMessage
			.Should()
			.Contain(
				"or:    SNAPSHOT_ACCEPT=1 dotnet test --project tests/Acme.Tests --treenode-filter \"/Acme.Tests/Acme.Tests.Rendering/DerivedHtmlTests/RendersTable\""
			);
	}

	[Test]
	public void WithoutIdentityTheFileNameIsTheClass()
	{
		var p = new TestProject();
		p.Match("x").Key.Should().Be("Acme.Tests/Rendering/HtmlTests/RendersTable");
	}

	[Test]
	public void ObjFallbackLayout()
	{
		var p = new TestProject(artifacts: false);
		var result = p.Match("x");
		result.FailureMessage.Should().Contain("Received: tests/Acme.Tests/obj/snapshots/received/Rendering/HtmlTests/RendersTable.received.txt");
	}

	[Test]
	public void ScrubbersRunBeforeComparing()
	{
		var p = new TestProject();
		p.AddVerified(Key, "id Guid_1\n");
		p.Match($"id {Guid.NewGuid()}", options: new SnapshotOptions().Scrub(Scrubbers.Guids)).Passed.Should().BeTrue();
	}

	[Test]
	public void BecauseIsPartOfTheFirstLine()
	{
		var p = new TestProject();
		p.AddVerified(Key, "a\n");
		p
			.Match("b", because: "the table changed")
			.FailureMessage
			.Should()
			.StartWith("Text did not match snapshot because the table changed: tests/Acme.Tests/.snapshots/");
	}

	[Test]
	public void WritesStayInsideTheSnapshotFolders()
	{
		var p = new TestProject();
		var scoped = p.Engine.ScopeFor(p.Project);
		var outside = p.Fs.Path.Combine(TestProject.Root, "src", "evil.txt");
		var write = () => scoped.File.WriteAllText(outside, "x");
		write.Should().Throw<ScopedFileSystemException>();

		var hidden = p.Fs.Path.Combine(p.Project.ReceivedDirectory, ".env");
		var hiddenWrite = () => scoped.File.WriteAllText(hidden, "x");
		hiddenWrite.Should().Throw<ScopedFileSystemException>();
	}

	[Test]
	public void ManifestListsTouchedFilesSortedOnceTheRunEnds()
	{
		var p = new TestProject();
		p.AddVerified(Key, "same\n");
		p.Match("same");
		p.Match("other", name: "b", line: 20);
		p.Match("other", name: "a", line: 30);
		var manifest = p.Fs.Path.Combine(p.Project.ReceivedDirectory, RunManifest.FileName);
		p.Fs.File.Exists(manifest).Should().BeFalse("nothing is written per assertion");

		p.Engine.FlushManifests();

		var (files, started) = RunManifest.Read(p.Read(manifest));
		files.Should().BeEquivalentTo(
			"Rendering/HtmlTests/RendersTable.verified.txt",
			"Rendering/HtmlTests/RendersTable.a.verified.txt",
			"Rendering/HtmlTests/RendersTable.b.verified.txt"
		);
		started.Should().NotBeNull();
		p.Read(manifest).Split('\n').Skip(1).Where(l => l.Length > 0).Should().BeInAscendingOrder(StringComparer.Ordinal);

		// Many keys added in reverse, so an unsorted set would almost surely show.
		var many = new TestProject();
		for (var i = 50; i > 0; i--)
			many.Match("x", name: $"n{i:00}", line: i);
		many.Engine.FlushManifests();
		many
			.Read(many.Fs.Path.Combine(many.Project.ReceivedDirectory, RunManifest.FileName))
			.Split('\n')
			.Skip(1)
			.Where(l => l.Length > 0)
			.Should()
			.HaveCount(50)
			.And
			.BeInAscendingOrder(StringComparer.Ordinal);
		p.Fs.File.Exists(p.Fs.Path.Combine(p.Project.ReceivedDirectory, ReceivedMarker.FileName)).Should().BeTrue();
	}

	[Test]
	public void ManifestReplacesThePreviousRunsAndFlushesOnce()
	{
		var p = new TestProject();
		var manifest = p.Fs.Path.Combine(p.Project.ReceivedDirectory, RunManifest.FileName);
		p.Fs.AddFile(manifest, new("# old\nGone/Test.verified.txt\n"));
		p.AddVerified(Key, "same\n");
		p.Match("same");
		p.Engine.FlushManifests();
		p.Fs.File.WriteAllText(manifest, "changed by someone else");
		p.Engine.FlushManifests();

		p.Read(manifest).Should().Be("changed by someone else");
	}

	[Test]
	public void ManifestOfAPassingRunStillMakesTheProjectFindable()
	{
		var p = new TestProject();
		p.AddVerified(Key, "same\n");
		p.Match("same");
		p.Engine.FlushManifests();
		RunManifest
			.Read(p.Read(p.Fs.Path.Combine(p.Project.ReceivedDirectory, RunManifest.FileName)))
			.Files
			.Should()
			.Equal("Rendering/HtmlTests/RendersTable.verified.txt");
		p.Fs.File.Exists(p.Fs.Path.Combine(p.Project.ReceivedDirectory, ReceivedMarker.FileName)).Should().BeTrue("the CLI finds projects by their marker");
	}

	[Test]
	public void RacingScrubberRegistrationsAreAllKept()
	{
		var before = SnapshotDefaults.Scrubbers.Count;

		static string Same(string value) => value;
		// Dedicated threads released together, so the registrations really collide.
		using var start = new ManualResetEventSlim();
		var threads = Enumerable
			.Range(0, Math.Max(4, Environment.ProcessorCount))
			.Select(_ => new Thread(() =>
			{
				start.Wait();
				for (var i = 0; i < 300; i++)
					SnapshotDefaults.AddScrubber(Same);
			}))
			.ToList();
		threads.ForEach(t => t.Start());
		start.Set();
		threads.ForEach(t => t.Join());
		var after = SnapshotDefaults.Scrubbers.Count;
		SnapshotDefaults.ClearScrubbers();
		after.Should().Be(before + (threads.Count * 300));
	}

	// Regression: the lost first version pruned received folders at the start of a run, which collided with parallel
	// writes. Runs never prune now; many tests writing into one received tree at once must all land.
	[Test]
	public async Task ParallelFailuresAllWriteTheirReceivedFiles()
	{
		var root = Directory.CreateTempSubdirectory("snapshots-stress-").FullName;
		try
		{
			IFileSystem fs = new FileSystem();
			var projectDir = Path.Combine(root, "tests", "Acme.Tests");
			Directory.CreateDirectory(projectDir);
			var project = new SnapshotProject(
				"Acme.Tests",
				projectDir,
				Path.Combine(projectDir, ".snapshots"),
				Path.Combine(root, ".artifacts", ".snapshots", "received", "Acme.Tests"),
				root
			);
			var engine = new SnapshotEngine(fs, _ => null, () => null, (_, _) => project);
			var file = Path.Combine(projectDir, "Rendering", "HtmlTests.cs");

			var results = await Task.WhenAll(
				Enumerable.Range(0, 200).Select(
					i => Task.Run(() => engine.Match($"value {i}", SnapshotFormats.Text, new SnapshotCallSite(file, $"Test{i % 20}", i), $"n{i}", null, null))
				)
			);

			foreach (var result in results)
			{
				result.Passed.Should().BeFalse();
				var n = result.Key[(result.Key.LastIndexOf(".n", StringComparison.Ordinal) + 2)..];
				File.ReadAllText(result.ReceivedPath).Should().Be($"value {n}\n");
			}
			Directory.EnumerateFiles(project.ReceivedDirectory, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
