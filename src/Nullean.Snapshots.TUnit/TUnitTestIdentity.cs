using System.ComponentModel;

namespace Nullean.Snapshots.TUnit;

/// <summary>
/// Supplies the running TUnit test's identity to Nullean.Snapshots: the runtime class (so inherited tests get their own
/// snapshots), the method, the arguments and a <c>--treenode-filter</c> for the failure message.
/// </summary>
public sealed class TUnitTestIdentity : ISnapshotTestIdentityProvider
{
	/// <summary>The shared instance.</summary>
	public static TUnitTestIdentity Instance { get; } = new();

	private TUnitTestIdentity() { }

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
			var details = TestContext.Current?.Metadata.TestDetails;
			if (details is null)
				return null;
			var type = details.ClassType;
			return new SnapshotTestIdentity(
				details.TestId,
				ClassName(type),
				details.MethodName,
				[.. details.TestClassArguments, .. details.TestMethodArguments],
				type.Assembly,
				TreeNodeFilter(type, details.MethodName),
				details.TestClassArguments.Length);
		}
	}

	/// <summary>The class folder name: nested classes joined with <c>.</c>, generic arguments appended with <c>_</c>.</summary>
	internal static string ClassName(Type type)
	{
		var name = type.IsNested && type.DeclaringType is { } outer ? ClassName(outer) + "." + Plain(type) : Plain(type);
		return type.IsGenericType && !type.IsGenericTypeDefinition
			? name + "_" + string.Join("_", type.GetGenericArguments().Select(Plain))
			: name;
	}

	/// <summary><c>/{Assembly}/{Namespace}/{Class}/{Method}</c>, the shape Microsoft.Testing.Platform's filter takes.</summary>
	internal static string TreeNodeFilter(Type type, string methodName) =>
		$"/{type.Assembly.GetName().Name}/{type.Namespace}/{Plain(type)}/{methodName}";

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
