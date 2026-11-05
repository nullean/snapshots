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

	public SnapshotResult MatchFile(string actual, string sourceFile, SnapshotOptions? options = null, string? because = null) =>
		Engine.MatchFile(actual, SnapshotFormats.Text, sourceFile, options, because);

	public SnapshotResult MatchInline(
		string actual,
		string? expected,
		string? expression = null,
		string method = "MatchInlineSnapshot",
		int line = 10,
		string member = "RendersTable",
		SnapshotOptions? options = null,
		string? because = null
	) => Engine.MatchInline(actual, expected, SnapshotFormats.Text, new InlineSnapshotCallSite(Site(member, line), method, expression), options, because);

	/// <summary>Writes the test file the inline assertions are in, so accepting has something to rewrite.</summary>
	public void AddSource(string text, bool bom = false) =>
		Fs.AddFile(TestFile, new MockFileData([.. bom ? new byte[] { 0xEF, 0xBB, 0xBF } : [], .. new System.Text.UTF8Encoding(false).GetBytes(text)]));

	public string VerifiedFile(string keyWithinProject, string extension = "txt") =>
		SnapshotPath.File(Fs, Project.SnapshotsDirectory, keyWithinProject, SnapshotPath.VerifiedInfix, extension);

	public string ReceivedFile(string keyWithinProject, string extension = "txt") =>
		SnapshotPath.File(Fs, Project.ReceivedDirectory, keyWithinProject, SnapshotPath.ReceivedInfix, extension);

	public void AddVerified(string keyWithinProject, string content) =>
		Fs.AddFile(VerifiedFile(keyWithinProject), new MockFileData(content));

	public string Read(string path) => Fs.File.ReadAllText(path);

	public static SnapshotTestIdentity TUnitLike(string className = "HtmlTests", string method = "RendersTable", params object?[] arguments) =>
		new($"{className}.{method}({string.Join(",", arguments)})", className, method, arguments, null, $"/Acme.Tests/Acme.Tests.Rendering/{className}/{method}");

	private sealed class Provider(TestProject owner) : ISnapshotTestIdentityProvider
	{
		public SnapshotTestIdentity? Current => owner.Identity;
	}
}
