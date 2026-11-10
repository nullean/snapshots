using System.Reflection;

namespace Nullean.HttpFileParser;

/// <summary>How <see cref="HttpFileRunner"/> resolves variables and sends requests.</summary>
public sealed class HttpFileRunOptions
{
	/// <summary>The environment to select from <c>http-client.env.json</c>; <see langword="null"/> uses only <c>$shared</c>.</summary>
	public string? Environment { get; init; }

	/// <summary>Variables that beat file variables and the environment, like <c>--var host=...</c> on a command line.</summary>
	public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>
	/// Answers secrets by name: consulted first for <c>AspnetUserSecrets</c> secret names, and the only source for other
	/// providers (<c>AzureKeyVault</c>, <c>Encrypted</c>, given the <c>secretName</c> or else the variable name) and for
	/// <c>{{$secret Name}}</c>. Return <see langword="null"/> for an unknown name.
	/// </summary>
	public Func<string, string?>? Secrets { get; init; }

	/// <summary>The user secrets id for <c>AspnetUserSecrets</c>; when unset it comes from <see cref="SecretsAssembly"/>, else from the nearest project file.</summary>
	public string? UserSecretsId { get; init; }

	/// <summary>An assembly whose <c>[UserSecretsId]</c> attribute names the user secrets id, typically the app under test's.</summary>
	public Assembly? SecretsAssembly { get; init; }

	/// <summary>The folder holding <c>{id}/secrets.json</c>; defaults to where <c>dotnet user-secrets</c> keeps them.</summary>
	public string? UserSecretsRoot { get; init; }

	/// <summary>The clock for <c>$datetime</c>, <c>$localDatetime</c>, <c>$timestamp</c> and <c>$isoTimestamp</c>.</summary>
	public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

	/// <summary>The randomness for <c>$guid</c> and <c>$randomInt</c>; pass a seeded instance for repeatable runs. Defaults to a new unseeded one per run.</summary>
	public Random? Random { get; init; }

	/// <summary>
	/// A read-only run, for a live service: a request whose method is not GET, HEAD or OPTIONS is not sent, and fails the
	/// run, unless it carries <c># @readonly</c>.
	/// </summary>
	public bool ReadOnly { get; init; }

	/// <summary>How many redirects a request may follow before the run fails. Defaults to 10.</summary>
	public int MaxRedirects { get; init; } = 10;
}
