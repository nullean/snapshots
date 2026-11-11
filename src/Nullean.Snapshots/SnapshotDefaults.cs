using System.Text.RegularExpressions;

namespace Nullean.Snapshots;

/// <summary>
/// Process-wide snapshot settings. Configure them once, before the first test runs: from a
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> or a TUnit <c>[Before(Assembly)]</c> hook.
/// </summary>
public static class SnapshotDefaults
{
	private static Func<string, string>[] AllScrubbers = [];
	private static ISnapshotTestIdentityProvider? IdentityProvider;

	/// <summary>Scrubbers applied to every snapshot, in the order they were added.</summary>
	public static IReadOnlyList<Func<string, string>> Scrubbers => Volatile.Read(ref AllScrubbers);

	/// <summary>The registered source of test identity, if any. See <see cref="UseTestIdentity"/>.</summary>
	public static ISnapshotTestIdentityProvider? TestIdentity => Volatile.Read(ref IdentityProvider);

	/// <summary>Adds a scrubber applied to every snapshot.</summary>
	public static void AddScrubber(Func<string, string> scrubber)
	{
		ArgumentNullException.ThrowIfNull(scrubber);
		// Copy-on-write with compare-and-swap: readers never block, and racing writers retry instead of locking.
		Func<string, string>[] current, next;
		do
		{
			current = Volatile.Read(ref AllScrubbers);
			next = [.. current, scrubber];
		}
		while (Interlocked.CompareExchange(ref AllScrubbers, next, current) != current);
	}

	/// <summary>Adds a scrubber applied to every snapshot that replaces every match of <paramref name="pattern"/>.</summary>
	public static void AddScrubber(Regex pattern, string replacement) => AddScrubber(Nullean.Snapshots.Scrubbers.Regex(pattern, replacement));

	/// <summary>Removes every scrubber added through <see cref="AddScrubber(Func{string, string})"/>.</summary>
	public static void ClearScrubbers() => Volatile.Write(ref AllScrubbers, []);

	/// <summary>
	/// Registers where the current test's identity comes from. The <c>Nullean.Snapshots.TUnit</c> package does this
	/// for you. Without a provider a snapshot is named after its test file and calling member.
	/// </summary>
	public static void UseTestIdentity(ISnapshotTestIdentityProvider? provider) => Volatile.Write(ref IdentityProvider, provider);
}
