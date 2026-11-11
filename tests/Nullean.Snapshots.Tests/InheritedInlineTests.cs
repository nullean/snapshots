using AwesomeAssertions;

namespace Nullean.Snapshots.Tests;

/// <summary>#40: an inline snapshot in a test inherited through <c>[InheritsTests]</c> compares per derived class, the literal shared.</summary>
public abstract class InheritedInlineBase
{
	[Test]
	public void TheSharedLiteralHoldsForEveryDerivedClass() => "same in every class".Should().MatchInlineSnapshot("same in every class");
}

[InheritsTests]
public sealed class FirstInheritedInline : InheritedInlineBase;

[InheritsTests]
public sealed class SecondInheritedInline : InheritedInlineBase;
