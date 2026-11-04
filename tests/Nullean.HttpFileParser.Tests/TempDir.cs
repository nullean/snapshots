namespace Nullean.HttpFileParser.Tests;

/// <summary>A fresh folder under the system temp folder, with no project file above it.</summary>
internal static class TempDir
{
	public static string Create()
	{
		var path = Path.Combine(Path.GetTempPath(), "httpfileparser-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}
}
