using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using AwesomeAssertions.Primitives;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Json;

/// <summary>JSON assertions for AwesomeAssertions: snapshots of JSON text or serialized objects, and inline JSON.</summary>
public static class JsonAssertions
{
	private const string ReflectionReason = "Serializes with reflection; use the JsonTypeInfo<T> overload for trimming and native AOT.";

	/// <summary>
	/// Asserts that the JSON text matches its verified snapshot, stored pretty-printed as <c>.verified.json</c> and
	/// compared as a tree.
	/// </summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="diff">How trees are compared: key order, strict numbers, ignored paths.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers; they run on the pretty-printed JSON.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StringAssertions> MatchJsonSnapshot(
		this StringAssertions assertions,
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
		if (assertions.Subject is null)
			return FailNull(assertions);
		Match(assertions.Subject, diff, name, options, because, callerFilePath, callerMemberName, callerLineNumber);
		return new AndConstraint<StringAssertions>(assertions);
	}

	/// <summary>
	/// Asserts that the object, serialized with <paramref name="typeInfo"/>, matches its verified JSON snapshot. Safe for
	/// trimming and native AOT.
	/// </summary>
	/// <param name="assertions">The object assertions.</param>
	/// <param name="typeInfo">The serialization metadata for <typeparamref name="T"/>, e.g. from a source-generated context.</param>
	/// <param name="diff">How trees are compared.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<ObjectAssertions> MatchJsonSnapshot<T>(
		this ObjectAssertions assertions,
		JsonTypeInfo<T> typeInfo,
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
		ArgumentNullException.ThrowIfNull(typeInfo);
		var json = JsonSerializer.Serialize((T)assertions.Subject, typeInfo);
		Match(json, diff, name, options, because, callerFilePath, callerMemberName, callerLineNumber);
		return new AndConstraint<ObjectAssertions>(assertions);
	}

	/// <summary>Asserts that the object, serialized with <paramref name="serializerOptions"/>, matches its verified JSON snapshot.</summary>
	/// <param name="assertions">The object assertions.</param>
	/// <param name="serializerOptions">How to serialize; <see cref="JsonSerializerOptions.Default"/> when null.</param>
	/// <param name="diff">How trees are compared.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	[RequiresUnreferencedCode(ReflectionReason)]
	[RequiresDynamicCode(ReflectionReason)]
	public static AndConstraint<ObjectAssertions> MatchJsonSnapshot(
		this ObjectAssertions assertions,
		JsonSerializerOptions? serializerOptions = null,
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
		var subject = assertions.Subject;
		var json = subject is null
			? "null"
			: JsonSerializer.Serialize(subject, subject.GetType(), serializerOptions ?? JsonSerializerOptions.Default);
		Match(json, diff, name, options, because, callerFilePath, callerMemberName, callerLineNumber);
		return new AndConstraint<ObjectAssertions>(assertions);
	}

	/// <summary>Asserts that the JSON text equals <paramref name="expected"/> as a tree.</summary>
	/// <param name="assertions">The string assertions.</param>
	/// <param name="expected">The expected JSON.</param>
	/// <param name="diff">How trees are compared.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static AndConstraint<StringAssertions> BeJson(this StringAssertions assertions, string expected, TreeDiffOptions? diff = null, string because = "")
	{
		ArgumentNullException.ThrowIfNull(assertions);
		ArgumentNullException.ThrowIfNull(expected);
		if (assertions.Subject is null)
			return FailNull(assertions);

		SnapshotComparison comparison;
		try
		{
			comparison = JsonSnapshot.FormatWith(diff).Compare(JsonSnapshot.Pretty(expected), JsonSnapshot.Pretty(assertions.Subject));
		}
		catch (FormatException e)
		{
			SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, $"Expected valid JSON{Because(because)}, but: {e.Message}");
			return new AndConstraint<StringAssertions>(assertions);
		}

		var message = comparison.IsMatch
			? string.Empty
			: $"JSON did not match the expected JSON{Because(because)}:\n{SnapshotMessages.Describe(comparison)}";
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, comparison.IsMatch, message);
		return new AndConstraint<StringAssertions>(assertions);
	}

	private static void Match(string json, TreeDiffOptions? diff, string? name, SnapshotOptions? options, string because, string file, string member, int line) =>
		JsonSnapshot.Pretty(json).Should().MatchSnapshot(JsonSnapshot.FormatWith(diff), name, options, because, file, member, line);

	private static string Because(string because)
	{
		if (string.IsNullOrWhiteSpace(because))
			return string.Empty;
		because = because.Trim();
		return because.StartsWith("because ", StringComparison.OrdinalIgnoreCase) ? " " + because : " because " + because;
	}

	private static AndConstraint<StringAssertions> FailNull(StringAssertions assertions)
	{
		SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, false, "Expected JSON, but it was <null>.");
		return new AndConstraint<StringAssertions>(assertions);
	}
}
