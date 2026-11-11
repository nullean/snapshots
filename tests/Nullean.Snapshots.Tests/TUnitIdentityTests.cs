using System.IO.Abstractions;
using AwesomeAssertions;
using Nullean.Snapshots.TUnit;

namespace Nullean.Snapshots.Tests;

/// <summary>Runs inside real TUnit tests, so it checks what TUnit actually reports.</summary>
public class TUnitIdentityTests
{
	[Test]
	public void ModuleInitializerRegisteredTUnit() => SnapshotDefaults.TestIdentity.Should().BeSameAs(TUnitTestIdentity.Instance);

	[Test]
	public void ReportsClassMethodAndFilter()
	{
		var identity = TUnitTestIdentity.Instance.Current!;
		identity.ClassName.Should().Be(nameof(TUnitIdentityTests));
		identity.MethodName.Should().Be(nameof(ReportsClassMethodAndFilter));
		identity.Arguments.Should().BeEmpty();
		identity.TestAssembly.Should().BeSameAs(typeof(TUnitIdentityTests).Assembly);
		identity.TreeNodeFilter.Should().Be("/Nullean.Snapshots.Tests/Nullean.Snapshots.Tests/TUnitIdentityTests/ReportsClassMethodAndFilter");
	}

	[Test]
	[Arguments(1, "a")]
	[Arguments(2, "b")]
	public void ReportsArguments(int number, string text) => TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(number, text);

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

/// <summary>A fixture, injected rather than passed as data.</summary>
public sealed class IdentityFixture;

/// <summary>A second fixture type, for a class with more than one class-level source.</summary>
public sealed class OtherIdentityFixture;

/// <summary>Constructor-injected fixture, the shape from issue #13: the fixture stays out of the arguments.</summary>
[ClassDataSource<IdentityFixture>(Shared = SharedType.PerClass)]
public sealed class InjectedFixtureIdentity(IdentityFixture fixture)
{
	[Test]
	public void FixtureIsNotAnArgument()
	{
		fixture.Should().NotBeNull();
		TUnitTestIdentity.Instance.Current!.Arguments.Should().BeEmpty();
	}

	[Test]
	[Arguments(1)]
	[Arguments(2)]
	public void MethodDataIsStillAnArgument(int number) => TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(number);

	[Test]
	[Arguments(3)]
	public void SingleMethodDataIsStillAnArgument(int number) => TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(number);

	[Test]
	[MethodDataSource(nameof(Numbers))]
	public void MethodDataSourceIsStillAnArgument(int number) => TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(number);

	public static IEnumerable<int> Numbers() => [4];

	[Test]
	[ClassDataSource<OtherIdentityFixture>]
	public void InjectedMethodParameterIsNotAnArgument(OtherIdentityFixture other)
	{
		other.Should().NotBeNull();
		TUnitTestIdentity.Instance.Current!.Arguments.Should().BeEmpty();
	}
}

/// <summary>Constructor data, not a fixture: it still tells cases apart.</summary>
[Arguments("x")]
[Arguments("y")]
public sealed class ClassDataIdentity(string value)
{
	[Test]
	public void ClassDataIsAnArgument() => TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(value);
}

/// <summary>A single constructor data source is still data.</summary>
[Arguments("z")]
public sealed class SingleClassDataIdentity(string value)
{
	[Test]
	public void ClassDataIsAnArgument() => TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(value);
}

public class InjectedSourceTests
{
	[Test]
	public void ClassDataSourcesInject()
	{
		TUnitTestIdentity.IsInjected(new ClassDataSourceAttribute(typeof(IdentityFixture))).Should().BeTrue();
		TUnitTestIdentity.IsInjected(new ClassDataSourceAttribute<IdentityFixture>()).Should().BeTrue();
		TUnitTestIdentity.IsInjected(new ClassDataSourceAttribute<IdentityFixture, IdentityFixture>()).Should().BeTrue();
		TUnitTestIdentity.IsInjected(new ClassDataSourceAttribute<IdentityFixture, IdentityFixture, IdentityFixture>()).Should().BeTrue();
		TUnitTestIdentity.IsInjected(new ClassDataSourceAttribute<IdentityFixture, IdentityFixture, IdentityFixture, IdentityFixture>()).Should().BeTrue();
		TUnitTestIdentity
			.IsInjected(new ClassDataSourceAttribute<IdentityFixture, IdentityFixture, IdentityFixture, IdentityFixture, IdentityFixture>())
			.Should()
			.BeTrue();
	}

	[Test]
	public void DataSourcesDoNotInject()
	{
		TUnitTestIdentity.IsInjected(null).Should().BeFalse();
		TUnitTestIdentity.IsInjected(new ArgumentsAttribute(1)).Should().BeFalse();
		TUnitTestIdentity.IsInjected(new MethodDataSourceAttribute("Data")).Should().BeFalse();
	}
}

/// <summary>Two fixture sources make two variants of each test; the fixture is all that tells them apart.</summary>
[ClassDataSource<IdentityFixture>]
[ClassDataSource<OtherIdentityFixture>]
public sealed class TwoFixtureSourcesIdentity(object fixture)
{
	[Test]
	public void FixtureStaysAnArgument() => TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(fixture);

	[Test]
	[ClassDataSource<IdentityFixture>]
	[ClassDataSource<OtherIdentityFixture>]
	public void InjectedMethodFixtureStaysAnArgument(object other) => TUnitTestIdentity.Instance.Current!.Arguments.Should().Equal(fixture, other);
}
