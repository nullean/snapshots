// A stand-in with the full name of Microsoft.Extensions.Configuration's attribute: the runner finds it by name,
// without a package reference, which is what this proves.
[assembly: Microsoft.Extensions.Configuration.UserSecrets.UserSecretsId("test-assembly-secrets-id")]

#pragma warning disable IDE0130 // the namespace is the point
namespace Microsoft.Extensions.Configuration.UserSecrets;

[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class UserSecretsIdAttribute(string userSecretsId) : Attribute
{
	public string UserSecretsId { get; } = userSecretsId;
}
