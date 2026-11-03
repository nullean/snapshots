using System.Reflection;

namespace Nullean.Snapshots;

/// <summary>Supplies the identity of the test that is currently running.</summary>
public interface ISnapshotTestIdentityProvider
{
	/// <summary>The running test, or <see langword="null"/> when called outside a test.</summary>
	SnapshotTestIdentity? Current { get; }
}

/// <summary>Who is asking for a snapshot.</summary>
/// <param name="TestId">Unique per test case within a run; two calls with the same key and different ids collide.</param>
/// <param name="ClassName">
/// The runtime test class, which names the snapshot's class folder. It differs from the file's class for inherited
/// tests.
/// </param>
/// <param name="MethodName">The test method.</param>
/// <param name="Arguments">The test's class and method arguments; non-empty arguments add a hash to the file name.</param>
/// <param name="TestAssembly">The test assembly, read for the project's snapshot folders.</param>
/// <param name="TreeNodeFilter">A <c>--treenode-filter</c> value that selects this test, if the framework has one.</param>
/// <param name="ClassArgumentCount">
/// How many leading entries of <paramref name="Arguments"/> come from the test class's constructor. A provider that
/// cannot tell class arguments from method arguments leaves it at 0.
/// </param>
public sealed record SnapshotTestIdentity(
	string TestId,
	string ClassName,
	string MethodName,
	IReadOnlyList<object?> Arguments,
	Assembly? TestAssembly,
	string? TreeNodeFilter,
	int ClassArgumentCount = 0
)
{
	/// <summary>
	/// The arguments that name the snapshot: all of them, or only the method's when <paramref name="ignoreClassArguments"/> is set.
	/// </summary>
	public IReadOnlyList<object?> NamingArguments(bool ignoreClassArguments) =>
		ignoreClassArguments && ClassArgumentCount > 0 ? [.. Arguments.Skip(ClassArgumentCount)] : Arguments;
}

/// <summary>Where a snapshot assertion was written, filled in by the compiler.</summary>
/// <param name="FilePath">From <see cref="System.Runtime.CompilerServices.CallerFilePathAttribute"/>.</param>
/// <param name="MemberName">From <see cref="System.Runtime.CompilerServices.CallerMemberNameAttribute"/>.</param>
/// <param name="LineNumber">From <see cref="System.Runtime.CompilerServices.CallerLineNumberAttribute"/>.</param>
public readonly record struct SnapshotCallSite(string FilePath, string MemberName, int LineNumber);
