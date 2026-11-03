using System.IO.Abstractions;
using AwesomeAssertions;
using Nullean.Snapshots.TUnit;

namespace Nullean.Snapshots.Tests;

/// <summary>Runs inside real TUnit tests, so it checks what TUnit actually reports.</summary>
public class TUnitIdentityTests
{
	[Test]
	public void ModuleInitializerRegisteredTUnit() =>
		SnapshotDefaults.TestIdentity.Should().BeSameAs(TUnitTestIdentity.Instance);

	[Test]
	public void ReportsClassMethodAndFilter()
	{
		var identity = TUnitTestIdentity.Instance.Current!;
		identity.ClassName.Should().Be(nameof(TUnitIdentityTests));
		identity.MethodName.Should().Be(nameof(ReportsClassMethodAndFilter));
		identity.Arguments.Should().BeEmpty();
		identity.ClassArgumentCount.Should().Be(0);
		identity.TestAssembly.Should().BeSameAs(typeof(TUnitIdentityTests).Assembly);
		identity.TreeNodeFilter.Should().Be("/Nullean.Snapshots.Tests/Nullean.Snapshots.Tests/TUnitIdentityTests/ReportsClassMethodAndFilter");
	}

	[Test]
	[Arguments(1, "a")]
	[Arguments(2, "b")]
	public void ReportsArguments(int number, string text) =>
		TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(number, text);

	[Test]
	public void NestedAndGenericClassNames()
	{
		TUnitTestIdentity.ClassName(typeof(Outer.Inner)).Should().Be("TUnitIdentityTests.Outer.Inner");
		TUnitTestIdentity.ClassName(typeof(Generic<int>)).Should().Be("TUnitIdentityTests.Generic_Int32");
	}

	[Test]
	public void TargetsWroteTheProjectIntoTheAssembly()
	{
		var project = new SnapshotProjectResolver(new FileSystem()).FromMetadata(typeof(TUnitIdentityTests).Assembly);
		project.Should().NotBeNull();
		project!.Name.Should().Be("Nullean.Snapshots.Tests");
		project.SnapshotsDirectory.Should().Be(Path.Combine(project.Directory, ".snapshots"));
		project.ReceivedDirectory.Replace('\\', '/').Should().EndWith("/.artifacts/.snapshots/received/Nullean.Snapshots.Tests");
		File.Exists(Path.Combine(project.Directory, "Nullean.Snapshots.Tests.csproj")).Should().BeTrue();
	}

	public static class Outer
	{
		public sealed class Inner;
	}

	public sealed class Generic<T>;
}

/// <summary>A class that receives data through its constructor.</summary>
[Arguments("first")]
[Arguments("second")]
public sealed class ClassArgumentIdentity(string value)
{
	[Test]
	[Arguments(1)]
	public void SeparatesClassFromMethodArguments(int number)
	{
		var identity = TUnitTestIdentity.Instance.Current!;
		identity.ClassArgumentCount.Should().Be(1);
		identity.Arguments.Should().Equal(value, number);
		identity.NamingArguments(ignoreClassArguments: true).Should().Equal(number);
		identity.NamingArguments(ignoreClassArguments: false).Should().Equal(value, number);
	}
}

/// <summary>A class that receives a fixture through its constructor, the case that adds a hash to every file name.</summary>
[ClassDataSource<InjectedFixture>(Shared = SharedType.PerClass)]
public sealed class FixtureInjectedIdentity(InjectedFixture fixture)
{
	[Test]
	public void TheFixtureIsAClassArgument()
	{
		var identity = TUnitTestIdentity.Instance.Current!;
		identity.ClassArgumentCount.Should().Be(1);
		identity.Arguments.Should().ContainSingle().Which.Should().BeSameAs(fixture);
		identity.NamingArguments(ignoreClassArguments: true).Should().BeEmpty();
	}
}

public sealed class InjectedFixture;

/// <summary>A base class whose tests run again in a derived class through <c>[InheritsTests]</c>.</summary>
public abstract class InheritedIdentityBase
{
	[Test]
	public void RuntimeClassIsTheDerivedOne() =>
		TUnitTestIdentity.Instance.Current!.ClassName.Should().Be(GetType().Name).And.NotBe(nameof(InheritedIdentityBase));
}

[InheritsTests]
public sealed class FirstDerivedIdentity : InheritedIdentityBase;

[InheritsTests]
public sealed class SecondDerivedIdentity : InheritedIdentityBase;
