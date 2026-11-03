using System.IO.Abstractions.TestingHelpers;

namespace Nullean.Snapshots.Tests;

/// <summary>A test project on a mock file system, with an engine whose environment and identity a test controls.</summary>
internal sealed class TestProject
{
	public static string Root { get; } = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

	public MockFileSystem Fs { get; } = new();
	public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);
	public SnapshotTestIdentity? Identity { get; set; }
	public SnapshotProject Project { get; }
	public SnapshotEngine Engine { get; }

	public TestProject(bool artifacts = true)
	{
		Fs.AddDirectory(Fs.Path.Combine(Root, ".git"));
		var directory = Fs.Path.Combine(Root, "tests", "Acme.Tests");
		Fs.AddFile(Fs.Path.Combine(directory, "Acme.Tests.csproj"), new MockFileData("<Project />"));
		Project = new SnapshotProject(
			"Acme.Tests",
			directory,
			Fs.Path.Combine(directory, ".snapshots"),
			artifacts
				? Fs.Path.Combine(Root, ".artifacts", ".snapshots", "received", "Acme.Tests")
				: Fs.Path.Combine(directory, "obj", "snapshots", "received"),
			Root);
		Engine = new SnapshotEngine(Fs, k => Environment.GetValueOrDefault(k), () => new Provider(this), (_, _) => Project);
	}

	public string TestFile => Fs.Path.Combine(Project.Directory, "Rendering", "HtmlTests.cs");

	public SnapshotCallSite Site(string member = "RendersTable", int line = 10) => new(TestFile, member, line);

	public SnapshotResult Match(string actual, string? name = null, SnapshotOptions? options = null, string member = "RendersTable", int line = 10, string? because = null) =>
		Engine.Match(actual, SnapshotFormats.Text, Site(member, line), name, options, because);

	public string VerifiedFile(string keyWithinProject, string extension = "txt") =>
		SnapshotPath.File(Fs, Project.SnapshotsDirectory, keyWithinProject, SnapshotPath.VerifiedInfix, extension);

	public string ReceivedFile(string keyWithinProject, string extension = "txt") =>
		SnapshotPath.File(Fs, Project.ReceivedDirectory, keyWithinProject, SnapshotPath.ReceivedInfix, extension);

	public void AddVerified(string keyWithinProject, string content) =>
		Fs.AddFile(VerifiedFile(keyWithinProject), new MockFileData(content));

	public string Read(string path) => Fs.File.ReadAllText(path);

	public static SnapshotTestIdentity TUnitLike(string className = "HtmlTests", string method = "RendersTable", params object?[] arguments) =>
		new($"{className}.{method}({string.Join(",", arguments)})", className, method, arguments, null, $"/Acme.Tests/Acme.Tests.Rendering/{className}/{method}");

	/// <summary>An identity whose first entries come from the test class's constructor, as TUnit reports them.</summary>
	public static SnapshotTestIdentity TUnitLikeWithClassArguments(object?[] classArguments, params object?[] methodArguments)
	{
		object?[] all = [.. classArguments, .. methodArguments];
		return new(
			$"HtmlTests.RendersTable({string.Join(",", all)})", "HtmlTests", "RendersTable", all, null,
			"/Acme.Tests/Acme.Tests.Rendering/HtmlTests/RendersTable", classArguments.Length);
	}

	private sealed class Provider(TestProject owner) : ISnapshotTestIdentityProvider
	{
		public SnapshotTestIdentity? Current => owner.Identity;
	}
}
