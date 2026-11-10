using System.Runtime.CompilerServices;

namespace Nullean.Snapshots.Browser.Tests;

internal static class TestProcessSetup
{
	// CI points GITHUB_STEP_SUMMARY at a real file. Journey runs in these tests must not append their tables to it,
	// least of all several at once.
	[ModuleInitializer]
	internal static void ClearStepSummary() => Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
}
