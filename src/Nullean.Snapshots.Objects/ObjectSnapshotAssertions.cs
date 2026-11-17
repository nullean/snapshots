using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AwesomeAssertions.Primitives;
using Nullean.Snapshots.Trees;
using Nullean.Snapshots.Yaml;

namespace Nullean.Snapshots.Objects;

/// <summary>Object-graph assertions for AwesomeAssertions.</summary>
public static class ObjectSnapshotAssertions
{
	/// <summary>
	/// Asserts that the object, dumped by reflection with <see cref="ObjectSnapshot.Dump{T}(T, ObjectDumpOptions?)"/>, matches its verified
	/// snapshot, stored as <c>.verified.yml</c> and compared as a tree, so a new property is reported as an added path.
	/// </summary>
	/// <param name="assertions">The object assertions.</param>
	/// <param name="dump">How the object is read: ignored members and types, renderers, limits.</param>
	/// <param name="diff">How trees are compared: key order, strict numbers, ignored paths.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	[RequiresUnreferencedCode(ObjectSnapshot.ReflectionReason)]
	public static AndConstraint<ObjectAssertions> MatchObjectSnapshot(
		this ObjectAssertions assertions,
		ObjectDumpOptions? dump = null,
		TreeDiffOptions? diff = null,
		string? name = null,
		SnapshotOptions? options = null,
		string because = "",
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		ObjectSnapshot
			.Dump(assertions.Subject, dump)
			.Should()
			.MatchYamlSnapshot(diff, name, options, because, callerFilePath, callerMemberName, callerLineNumber);
		return new AndConstraint<ObjectAssertions>(assertions);
	}
}
