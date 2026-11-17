using System.Reflection;
using AwesomeAssertions;

namespace Nullean.Snapshots.PublicApi.Tests;

/// <summary>
/// Every package's public API, kept as a .verified.api snapshot: a change to one fails here, and its pull request shows
/// the API change as a diff of the file, with the version change it calls for in the failure.
/// </summary>
public class PackageApiTests
{
	public static IEnumerable<string> Packages() =>
		[
			"Nullean.HttpFileParser",
			"Nullean.Snapshots",
			"Nullean.Snapshots.AwesomeAssertions",
			"Nullean.Snapshots.Browser",
			"Nullean.Snapshots.Browser.Assertions",
			"Nullean.Snapshots.Browser.Console",
			"Nullean.Snapshots.Html",
			"Nullean.Snapshots.Http",
			"Nullean.Snapshots.Http.Assertions",
			"Nullean.Snapshots.Images",
			"Nullean.Snapshots.Images.Assertions",
			"Nullean.Snapshots.Json",
			"Nullean.Snapshots.Objects",
			"Nullean.Snapshots.PublicApi",
			"Nullean.Snapshots.TUnit",
			"Nullean.Snapshots.Yaml",
		];

	[Test]
	[MethodDataSource(nameof(Packages))]
	public void Keeps_its_public_api(string package) => System.Reflection.Assembly.Load(new AssemblyName(package)).Should().MatchApiSnapshot(name: package);
}
