using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AwesomeAssertions.Collections;
using AwesomeAssertions.Streams;

namespace Nullean.Snapshots.Images;

/// <summary>Image snapshot assertions for AwesomeAssertions.</summary>
public static class ImageAssertions
{
	/// <summary>
	/// Asserts that the PNG bytes match their verified snapshot, stored as <c>.verified.png</c> and compared by their
	/// pixels. On a mismatch the received image and a diff image are written next to each other. The bytes must be a
	/// valid PNG.
	/// </summary>
	/// <param name="assertions">The byte array assertions.</param>
	/// <param name="image">How closely the pixels must match.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<GenericCollectionAssertions<byte>> MatchImageSnapshot(
		this GenericCollectionAssertions<byte> assertions,
		ImageSnapshotOptions? image = null,
		string? name = null,
		string because = "",
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		var chain = assertions.CurrentAssertionChain;
		if (assertions.Subject is null)
		{
			SnapshotAssertions.FailUnless(chain, false, "Expected an image to match a snapshot, but it was <null>.");
		}
		else
		{
			var bytes = assertions.Subject as byte[] ?? [.. assertions.Subject];
			Match(chain, bytes, image, name, because, callerFilePath, callerMemberName, callerLineNumber);
		}

		return new AndConstraint<GenericCollectionAssertions<byte>>(assertions);
	}

	/// <summary>
	/// Asserts that the PNG in the stream, read from its current position to the end, matches its verified snapshot.
	/// See the byte array overload.
	/// </summary>
	/// <param name="assertions">The stream assertions.</param>
	/// <param name="image">How closely the pixels must match.</param>
	/// <param name="name">Tells apart several snapshots in one test.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <param name="callerMemberName">Filled in by the compiler.</param>
	/// <param name="callerLineNumber">Filled in by the compiler.</param>
	public static AndConstraint<StreamAssertions> MatchImageSnapshot(
		this StreamAssertions assertions,
		ImageSnapshotOptions? image = null,
		string? name = null,
		string because = "",
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "",
		[CallerLineNumber] int callerLineNumber = 0
	)
	{
		ArgumentNullException.ThrowIfNull(assertions);
		var chain = assertions.CurrentAssertionChain;
		if (assertions.Subject is null)
		{
			SnapshotAssertions.FailUnless(chain, false, "Expected an image to match a snapshot, but it was <null>.");
		}
		else
		{
			using var buffer = new MemoryStream();
			assertions.Subject.CopyTo(buffer);
			Match(chain, buffer.ToArray(), image, name, because, callerFilePath, callerMemberName, callerLineNumber);
		}

		return new AndConstraint<StreamAssertions>(assertions);
	}

	private static void Match(
		AwesomeAssertions.Execution.AssertionChain chain,
		byte[] bytes,
		ImageSnapshotOptions? image,
		string? name,
		string because,
		string callerFilePath,
		string callerMemberName,
		int callerLineNumber
	)
	{
		var result = Snapshot.Match(
			bytes,
			new ImageSnapshotFormat(image),
			new SnapshotCallSite(callerFilePath, callerMemberName, callerLineNumber),
			name,
			because
		);
		SnapshotAssertions.FailUnless(chain, result.Passed, result.FailureMessage ?? string.Empty);
	}
}
