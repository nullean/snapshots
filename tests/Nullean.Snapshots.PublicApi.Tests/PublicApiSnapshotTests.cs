using System.Collections.Immutable;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nullean.PublicApi;
using Nullean.PublicApi.Diff;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.PublicApi.Tests;

public class PublicApiSnapshotTests
{
	private const string Widget =
		"""
		namespace Lib.Drawing;
		public interface IShape { double Area { get; } void Draw(); }
		public class Widget : IShape
		{
			public Widget(string name) { }
			public double Area => 0;
			public void Draw() { }
			public string Name(int index) => "";
		}
		""";

	[Test]
	public void An_unchanged_api_matches() => PublicApiSnapshot.Compare(Api(Widget), Api(Widget)).IsMatch.Should().BeTrue();

	[Test]
	public void A_changed_api_lists_each_change_with_who_it_breaks()
	{
		var changed = Widget
			.Replace("void Draw();", "void Draw(); void Resize(double factor);")
			.Replace("public void Draw() { }", "public void Draw() { } public void Resize(double factor) { }")
			.Replace("public string Name(int index)", "public string Name(int position)");
		var comparison = PublicApiSnapshot.Compare(Api(Widget), Api(changed));

		comparison.IsMatch.Should().BeFalse();
		Lines(comparison).Should().MatchSnapshot();
	}

	[Test]
	public void A_type_that_is_not_an_extension_point_does_not_ask_for_a_major_version()
	{
		var changed = Widget.Replace("void Draw();", "void Draw(); void Resize(double factor);").Replace(
			"public void Draw() { }",
			"public void Draw() { } public void Resize(double factor) { }"
		);
		var comparison = PublicApiSnapshot.Compare(Api(Widget), Api(changed), new ApiComparisonOptions { NotExtensionPoints = ["Lib.Drawing.IShape"] });

		Lines(comparison).Should().MatchSnapshot();
	}

	[Test]
	public void The_same_api_written_differently_shows_the_text()
	{
		var verified = Api(Widget).Replace("  class Widget", "  public class Widget");
		var comparison = PublicApiSnapshot.Compare(verified, Api(Widget));

		comparison.IsMatch.Should().BeFalse();
		Lines(comparison).Should().MatchSnapshot();
	}

	[Test]
	public void A_verified_file_that_is_not_an_api_file_says_so() =>
		Lines(PublicApiSnapshot.Compare("not an api file\n", Api(Widget))).Should().MatchSnapshot();

	[Test]
	public void An_assembly_matches_its_verified_snapshot_and_fails_on_a_change()
	{
		static TestProject Project()
		{
			var project = new TestProject { Identity = TestProject.TUnitLike() };
			project.Fs.AddFile(project.VerifiedFile("Rendering/HtmlTests/RendersTable", "api"), new(Api(Widget)));
			return project;
		}

		var p = Project();
		WithEngine(p, () => Load(Widget).Should().MatchApiSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable")).Should().BeNull();
		p = Project();
		WithEngine(
			p,
			() => Load(Widget.Replace("public string Name(int index) => \"\";", ""))
				.Should()
				.MatchApiSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable")
		).Should().MatchSnapshot();
	}

	[Test]
	public void A_split_api_keeps_a_snapshot_per_namespace()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var source = Widget.Replace("namespace Lib.Drawing;", "namespace Lib.Drawing {") + "}\nnamespace Lib.Text { public class Label { } }\n";

		var message = WithEngine(
			p,
			() => Load(source).Should().MatchApiSnapshot(ApiSplit.Namespace, callerFilePath: p.TestFile, callerMemberName: "RendersTable")
		);

		message.Should().NotBeNull();
		message.Should().Contain("is new");
		p.Fs.File.Exists(p.ReceivedFile("Rendering/HtmlTests/RendersTable.Lib.Drawing", "api")).Should().BeTrue();
		p.Fs.File.Exists(p.ReceivedFile("Rendering/HtmlTests/RendersTable.Lib.Text", "api")).Should().BeTrue();
	}

	private static string Lines(SnapshotComparison comparison) =>
		string.Join(
			"\n",
			(comparison.Summary is null ? [] : new[] { comparison.Summary }).Concat(
				comparison.Differences.SelectMany(d => d).Concat((comparison.LineDiff ?? []).SelectMany(d => d))
			)
		) + "\n";

	private static readonly ImmutableArray<MetadataReference> References =
	[
		.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
			.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
	];

	private static byte[] Compile(string source, string assemblyName = "Lib")
	{
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[CSharpSyntaxTree.ParseText(source)],
			References,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
		);
		using var stream = new MemoryStream();
		var result = compilation.Emit(stream);
		if (!result.Success)
			throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
		return stream.ToArray();
	}

	private static string Api(string source) => ApiWriter.Write(ApiReader.Read(new MemoryStream(Compile(source))));

	/// <summary>
	/// Compiles to a file and loads it in a context of its own, so the assembly has a location to read and every version
	/// can be called Lib.
	/// </summary>
	private static Assembly Load(string source)
	{
		var path = Path.Combine(Path.GetTempPath(), "nullean-snapshots-publicapi", Guid.NewGuid().ToString("N"), "Lib.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, Compile(source));
		return new System.Runtime.Loader.AssemblyLoadContext(path, isCollectible: true).LoadFromAssemblyPath(path);
	}

	private static string? WithEngine(TestProject project, Action assertion)
	{
		Snapshot.EngineOverride.Value = project.Engine;
		try
		{
			assertion();
			return null;
		}
		catch (Exception e)
		{
			return e.Message;
		}
		finally
		{
			Snapshot.EngineOverride.Value = null;
		}
	}
}
