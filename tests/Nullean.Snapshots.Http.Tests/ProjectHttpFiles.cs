using Nullean.Snapshots.TUnit;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>The project's <c>.http</c> files, a test case each, against the shared test server.</summary>
public class ProjectHttpFiles
{
	internal static HttpFileSnapshotOptions Options => new()
	{
		Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = TestServer.Shared.Host },
		Headers = ["X-Request-Id"],
	};

	[Test]
	[HttpFiles]
	public Task Run(SnapshotFile file) => HttpFileTest.RunAsync(file, Options);
}
