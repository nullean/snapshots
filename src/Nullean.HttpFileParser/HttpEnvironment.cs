using System.Text;
using System.Text.Json;

namespace Nullean.HttpFileParser;

/// <summary>
/// The variables of one environment from <c>http-client.env.json</c>: <c>$shared</c> defaults, the named environment
/// over them, and <c>http-client.env.json.user</c> (Visual Studio) or <c>http-client.private.env.json</c> (JetBrains)
/// next to it over both.
/// </summary>
public sealed class HttpEnvironment
{
	/// <summary>The environment file name Visual Studio, REST Client and JetBrains share.</summary>
	public const string FileName = "http-client.env.json";

	/// <summary>Visual Studio's per-user override file, next to <see cref="FileName"/>.</summary>
	public const string UserFileName = "http-client.env.json.user";

	/// <summary>JetBrains' private override file, next to <see cref="FileName"/>.</summary>
	public const string PrivateFileName = "http-client.private.env.json";

	/// <summary>The name of the environment whose values are defaults for every other one.</summary>
	public const string SharedName = "$shared";

	private HttpEnvironment(string? name, string? directory, IReadOnlyList<string> names, IReadOnlyDictionary<string, HttpEnvironmentValue> variables)
	{
		Name = name;
		Directory = directory;
		Names = names;
		Variables = variables;
	}

	/// <summary>No environment: no variables.</summary>
	public static HttpEnvironment Empty { get; } = new(null, null, [], new Dictionary<string, HttpEnvironmentValue>(StringComparer.Ordinal));

	/// <summary>The selected environment; <see langword="null"/> when only <c>$shared</c> applies.</summary>
	public string? Name { get; }

	/// <summary>The folder the environment files were found in; <see langword="null"/> when there are none.</summary>
	public string? Directory { get; }

	/// <summary>The environments the files define, <c>$shared</c> excluded.</summary>
	public IReadOnlyList<string> Names { get; }

	/// <summary>The variables, by case-sensitive name.</summary>
	public IReadOnlyDictionary<string, HttpEnvironmentValue> Variables { get; }

	/// <summary>
	/// Finds <c>http-client.env.json</c> (or one of its override files) in the folder of <paramref name="httpFilePath"/> or
	/// the nearest folder above it, and selects <paramref name="name"/>. Throws <see cref="HttpFileParseException"/> when
	/// a file is invalid or <paramref name="name"/> is not defined. With no files and no name, returns <see cref="Empty"/>.
	/// </summary>
	public static HttpEnvironment Load(string httpFilePath, string? name = null)
	{
		ArgumentNullException.ThrowIfNull(httpFilePath);
		var start = Path.GetDirectoryName(Path.GetFullPath(httpFilePath));
		for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
		{
			if (File.Exists(Path.Combine(dir, FileName)) || File.Exists(Path.Combine(dir, UserFileName)) || File.Exists(Path.Combine(dir, PrivateFileName)))
				return LoadFrom(dir, name);
		}
		if (name is null)
			return Empty;
		throw new HttpFileParseException([
			new HttpFileError(httpFilePath, 1, $"environment '{name}' was selected but no {FileName} was found in '{start}' or above it")
		]);
	}

	/// <summary>Reads the environment files in <paramref name="directory"/> and selects <paramref name="name"/>.</summary>
	public static HttpEnvironment LoadFrom(string directory, string? name = null)
	{
		ArgumentNullException.ThrowIfNull(directory);
		var errors = new List<HttpFileError>();
		var files = new List<(string Path, bool Sensitive, Dictionary<string, Dictionary<string, HttpEnvironmentValue>> Environments)>();
		foreach (var (fileName, sensitive) in new[] { (FileName, false), (PrivateFileName, true), (UserFileName, true) })
		{
			var path = Path.Combine(directory, fileName);
			if (File.Exists(path))
				files.Add((path, sensitive, ParseFile(path, File.ReadAllBytes(path), sensitive, errors)));
		}
		if (errors.Count > 0)
			throw new HttpFileParseException(errors);

		var names = files.SelectMany(f => f.Environments.Keys).Where(n => n != SharedName).Distinct(StringComparer.Ordinal).ToList();
		if (name is not null && name != SharedName && !names.Contains(name, StringComparer.Ordinal))
		{
			var path = files.Count > 0 ? files[0].Path : Path.Combine(directory, FileName);
			var defined = names.Count == 0 ? "none are defined" : "defined: " + string.Join(", ", names);
			throw new HttpFileParseException([new HttpFileError(path, 1, $"environment '{name}' is not defined; {defined}")]);
		}

		// $shared from every file first, then the named environment from every file; later files override earlier ones.
		var variables = new Dictionary<string, HttpEnvironmentValue>(StringComparer.Ordinal);
		foreach (var environment in name is null or SharedName ? [SharedName] : new[] { SharedName, name })
		{
			foreach (var file in files)
			{
				if (!file.Environments.TryGetValue(environment, out var values))
					continue;
				foreach (var (key, value) in values)
					variables[key] = value;
			}
		}
		return new HttpEnvironment(name, directory, names, variables);
	}

	private static Dictionary<string, Dictionary<string, HttpEnvironmentValue>> ParseFile(string path, byte[] bytes, bool sensitive, List<HttpFileError> errors)
	{
		var result = new Dictionary<string, Dictionary<string, HttpEnvironmentValue>>(StringComparer.Ordinal);
		var span = bytes.AsSpan();
		if (span.StartsWith(Encoding.UTF8.Preamble))
			span = span[Encoding.UTF8.Preamble.Length..];
		var data = span.ToArray();

		int LineOf(long offset)
		{
			var line = 1;
			for (var i = 0; i < offset && i < data.Length; i++)
			{
				if (data[i] == (byte)'\n')
					line++;
			}
			return line;
		}

		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(data, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		}
		catch (JsonException e)
		{
			errors.Add(new HttpFileError(path, (int)(e.LineNumber ?? 0) + 1, $"invalid JSON: {e.Message}"));
			return result;
		}
		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				errors.Add(new HttpFileError(path, 1, "an environment file is a JSON object of environments, each an object of variables"));
				return result;
			}
			// JsonDocument does not expose offsets; a reader over the same bytes gives each property's line.
			var lines = PropertyLines(data, LineOf);
			var n = 0;
			foreach (var environment in document.RootElement.EnumerateObject())
			{
				var envLine = n < lines.Count ? lines[n] : 1;
				n++;
				if (environment.Value.ValueKind != JsonValueKind.Object)
				{
					errors.Add(new HttpFileError(path, envLine, $"environment '{environment.Name}' must be an object of variables"));
					n += CountNestedProperties(environment.Value);
					continue;
				}
				var values = new Dictionary<string, HttpEnvironmentValue>(StringComparer.Ordinal);
				foreach (var variable in environment.Value.EnumerateObject())
				{
					var line = n < lines.Count ? lines[n] : envLine;
					n++;
					n += CountNestedProperties(variable.Value);
					var value = ParseValue(path, environment.Name, variable, line, sensitive, errors);
					if (value is not null)
						values[variable.Name] = value;
				}
				result[environment.Name] = values;
			}
		}
		return result;
	}

	private static HttpEnvironmentValue? ParseValue(
		string path,
		string environment,
		JsonProperty variable,
		int line,
		bool sensitive,
		List<HttpFileError> errors
	)
	{
		var v = variable.Value;
		switch (v.ValueKind)
		{
			case JsonValueKind.String:
				return new HttpEnvironmentValue(variable.Name, v.GetString(), null, null, null, sensitive, path, line);
			case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
				return new HttpEnvironmentValue(variable.Name, v.GetRawText(), null, null, null, sensitive, path, line);
			case JsonValueKind.Null:
				return new HttpEnvironmentValue(variable.Name, "", null, null, null, sensitive, path, line);
			case JsonValueKind.Object when v.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String:
				string? Str(string key) => v.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
				var providerName = provider.GetString();
				var secretName = Str("secretName");
				if (providerName == "AspnetUserSecrets" && string.IsNullOrEmpty(secretName))
				{
					errors.Add(
						new HttpFileError(path, line, $"'{variable.Name}' in environment '{environment}': the AspnetUserSecrets provider needs a 'secretName'")
					);
					return null;
				}
				return new HttpEnvironmentValue(variable.Name, Str("value"), providerName, secretName, Str("resourceId"), true, path, line);
			default:
				errors.Add(
					new HttpFileError(
						path,
						line,
						$"'{variable.Name}' in environment '{environment}' must be a string, a number, a boolean or a secret ({{\"provider\": ..., \"secretName\": ...}})"
					)
				);
				return null;
		}
	}

	private static int CountNestedProperties(JsonElement element)
	{
		var count = 0;
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (var p in element.EnumerateObject())
				count += 1 + CountNestedProperties(p.Value);
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in element.EnumerateArray())
				count += CountNestedProperties(item);
		}
		return count;
	}

	/// <summary>The line of every property name in document order, at any depth.</summary>
	private static List<int> PropertyLines(byte[] data, Func<long, int> lineOf)
	{
		var lines = new List<int>();
		var reader = new Utf8JsonReader(data, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.PropertyName)
				lines.Add(lineOf(reader.TokenStartIndex));
		}
		return lines;
	}
}

/// <summary>A variable from an environment file: a plain value or a secret reference.</summary>
public sealed class HttpEnvironmentValue
{
	internal HttpEnvironmentValue(string name, string? value, string? provider, string? secretName, string? resourceId, bool sensitive, string source, int line)
	{
		Name = name;
		Value = value;
		Provider = provider;
		SecretName = secretName;
		ResourceId = resourceId;
		IsSensitive = sensitive;
		Source = source;
		Line = line;
	}

	/// <summary>The variable name.</summary>
	public string Name { get; }

	/// <summary>The plain value; for a secret, the <c>value</c> property if it has one (the <c>Encrypted</c> provider's ciphertext).</summary>
	public string? Value { get; }

	/// <summary><c>AspnetUserSecrets</c>, <c>AzureKeyVault</c>, <c>Encrypted</c> or another provider; <see langword="null"/> for a plain value.</summary>
	public string? Provider { get; }

	/// <summary>The secret's name for its provider (<c>Shop:ApiKey</c>).</summary>
	public string? SecretName { get; }

	/// <summary>The Key Vault resource id, for <c>AzureKeyVault</c>.</summary>
	public string? ResourceId { get; }

	/// <summary>Whether a run redacts the value: secrets, and values from the <c>.user</c> or <c>.private</c> file.</summary>
	public bool IsSensitive { get; }

	/// <summary>The file the value came from.</summary>
	public string Source { get; }

	/// <summary>The 1-based line of the variable in <see cref="Source"/>.</summary>
	public int Line { get; }

	/// <summary>The name and where it came from; never the value.</summary>
	public override string ToString() => $"{Name} ({Source}:{Line})";
}
