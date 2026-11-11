using System.Runtime.CompilerServices;

namespace Nullean.Snapshots.Browser.Tests;

internal static class TestProcessSetup
{
	// CI points GITHUB_STEP_SUMMARY at a real file. Journey runs in these tests must not append their tables to it,
	// least of all several at once.
	[ModuleInitializer]
	internal static void ClearStepSummary() => Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);

	// CI sets GITHUB_ACTIONS, which makes a run print workflow commands. Runs in these tests do so only when a test asks.
	[ModuleInitializer]
	internal static void ClearGitHubActions() => Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);

	// A failure screenshot is drawn with half blocks, which need no terminal: detection is forced, so no run in these tests
	// probes the terminal or writes graphics escape sequences to it.
	[ModuleInitializer]
	internal static void ForceHalfBlockImages() => Environment.SetEnvironmentVariable("NULLEAN_TERMINAL_IMAGES", "blocks");
}
