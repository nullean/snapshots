using System.ComponentModel;
using System.Reflection;

namespace Nullean.Snapshots.TUnit;

/// <summary>
/// Supplies the running TUnit test's identity to Nullean.Snapshots: the runtime class (so inherited tests get their own
/// snapshots), the method, the arguments and a <c>--treenode-filter</c> for the failure message.
/// </summary>
public sealed class TUnitTestIdentity : ISnapshotTestIdentityProvider
{
	/// <summary>The shared instance.</summary>
	public static TUnitTestIdentity Instance { get; } = new();

	private TUnitTestIdentity()
	{ }

	/// <summary>
	/// Makes TUnit the source of test identity. Referencing this package calls it for you from a module initializer
	/// compiled into the test project; set the MSBuild property <c>SnapshotsTUnitAutoRegister</c> to <c>false</c> to opt
	/// out and call it yourself.
	/// </summary>
	public static void Register() => SnapshotDefaults.UseTestIdentity(Instance);

	/// <inheritdoc />
	public SnapshotTestIdentity? Current
	{
		get
		{
			var metadata = TestContext.Current?.Metadata;
			var details = metadata?.TestDetails;
			if (metadata is null || details is null)
				return null;
			var type = details.ClassType;
			return new SnapshotTestIdentity(
				details.TestId,
				ClassName(type),
				details.MethodName,
				[
					.. IsInjected(metadata.ClassDataSource) && IsOnlySource(type) ? [] : details.TestClassArguments,
					.. IsInjected(metadata.MethodDataSource) && IsOnlySource(TestMethod(details)) ? [] : details.TestMethodArguments,
				],
				type.Assembly,
				TreeNodeFilter(type, details.MethodName)
			);
		}
	}

	private static readonly Type[] InjectingGenericSources =
	[
		typeof(ClassDataSourceAttribute<>),
		typeof(ClassDataSourceAttribute<,>),
		typeof(ClassDataSourceAttribute<,,>),
		typeof(ClassDataSourceAttribute<,,,>),
		typeof(ClassDataSourceAttribute<,,,,>),
	];

	/// <summary>
	/// True when <paramref name="source"/> injects dependencies rather than supplying test data: a
	/// <c>ClassDataSource</c> constructs an instance of a type (a fixture), so its arguments do not tell cases apart and
	/// stay out of the snapshot's arguments hash.
	/// </summary>
	internal static bool IsInjected(IDataSourceAttribute? source)
	{
		if (source is null)
			return false;
		if (source is ClassDataSourceAttribute)
			return true;
		var type = source.GetType();
		return type.IsGenericType && Array.IndexOf(InjectingGenericSources, type.GetGenericTypeDefinition()) >= 0;
	}

	/// <summary>
	/// True when <paramref name="member"/> declares a single data source. Two sources on one level make two variants
	/// of the test that only the injected instance tells apart, so its arguments stay in the hash. Unknown counts as
	/// more than one.
	/// </summary>
	private static bool IsOnlySource(MemberInfo? member)
	{
		if (member is null)
			return false;
		var count = 0;
		foreach (var attribute in member.GetCustomAttributes(inherit: true))
		{
			if (attribute is IDataSourceAttribute && ++count > 1)
				return false;
		}

		return count == 1;
	}

	/// <summary>The test method, reached through its first parameter: method arguments imply there is one.</summary>
	private static MethodBase? TestMethod(TestDetails details) =>
		details.MethodMetadata.Parameters is [var first, ..] ? first.ReflectionInfo.Member as MethodBase : null;

	/// <summary>The class folder name: nested classes joined with <c>.</c>, generic arguments appended with <c>_</c>.</summary>
	internal static string ClassName(Type type)
	{
		var name = type.IsNested && type.DeclaringType is { } outer ? ClassName(outer) + "." + Plain(type) : Plain(type);
		return type.IsGenericType && !type.IsGenericTypeDefinition ? name + "_" + string.Join("_", type.GetGenericArguments().Select(Plain)) : name;
	}

	/// <summary><c>/{Assembly}/{Namespace}/{Class}/{Method}</c>, the shape Microsoft.Testing.Platform's filter takes.</summary>
	internal static string TreeNodeFilter(Type type, string methodName) => $"/{type.Assembly.GetName().Name}/{type.Namespace}/{Plain(type)}/{methodName}";

	private static string Plain(Type type)
	{
		var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
		return tick < 0 ? type.Name : type.Name[..tick];
	}
}

/// <summary>Called by the module initializer the package adds to the test project. Not for direct use.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TUnitRegistration
{
	/// <summary>Registers <see cref="TUnitTestIdentity"/> unless another provider is already registered.</summary>
	public static void Initialize()
	{
		if (SnapshotDefaults.TestIdentity is null)
			TUnitTestIdentity.Register();
	}
}
