using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AwesomeAssertions.Types;
using Nullean.PublicApi;
using Nullean.PublicApi.Diff;

namespace Nullean.Snapshots.PublicApi;

/// <summary>Public API snapshots for AwesomeAssertions.</summary>
public static class PublicApiAssertions
{
	/// <summary>
	/// Asserts that the assembly's public API matches its verified snapshot, stored as <c>.verified.api</c>: C#, one
	/// declaration per line. A failure lists each change by path, the version change it calls for, who it breaks and
	/// where in the verified file.
	/// </summary>
	/// <param name="assertions">The assembly assertions, from <c>typeof(MyType).Assembly.Should()</c>.</param>
	/// <param name="split">
	/// Keep a snapshot per namespace, or per namespace's first two parts, for a large assembly; each is named after it.
	/// </param>
	/// <param name="comparison">How changes are classified, such as types that are not extension points.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<AssemblyAssertions> MatchApiSnapshot(
		this AssemblyAssertions assertions,
		ApiSplit split = ApiSplit.None,
		ApiComparisonOptions? comparison = null,
		string? name = null,
		SnapshotOptions? options = null,
		string because = "",
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		var chain = assertions.CurrentAssertionChain;
		if (assertions.Subject is not Assembly assembly)
		{
			SnapshotAssertions.FailUnless(chain, false, "Expected an assembly to match a public API snapshot, but it was <null>.");
			return new AndConstraint<AssemblyAssertions>(assertions);
		}
		// a single-file app's assemblies have no location; that fails with a message below rather than reading nothing
#pragma warning disable IL3000
		var location = assembly.Location;
#pragma warning restore IL3000
		if (string.IsNullOrEmpty(location))
		{
			// a name is not a format string: braces in it must not break the message
			SnapshotAssertions.FailUnless(
				chain,
				false,
				$"Expected {assembly.GetName().Name} to match a public API snapshot, but it has no file to read, as in a single-file app."
			);
			return new AndConstraint<AssemblyAssertions>(assertions);
		}

		var format = PublicApiSnapshot.FormatWith(comparison);
		var api = ApiReader.Read(location);
		if (split == ApiSplit.None)
			ApiWriter.Write(api).Should().MatchSnapshot(format, name, options, because, callerFilePath, callerMemberName, callerLineNumber);
		else
		{
			// every namespace is checked and written, and their failures reported together, not only the first
			using var scope = new AwesomeAssertions.Execution.AssertionScope();
			foreach (var file in ApiWriter.Write(api, split))
				file
					.Text
					.Should()
					.MatchSnapshot(
						format,
						name is null ? file.Name : name + "." + file.Name,
						options,
						because,
						callerFilePath,
						callerMemberName,
						callerLineNumber
					);
		}
		return new AndConstraint<AssemblyAssertions>(assertions);
	}
}
