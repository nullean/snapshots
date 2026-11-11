using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Nullean.HttpFileParser;

/// <summary>A reference that cannot be resolved, at a line of the <c>.http</c> file.</summary>
internal sealed class ResolveException(int line, string message) : Exception(message)
{
	public int Line { get; } = line;
}

/// <summary>Resolves <c>{{ }}</c> references during a run, recording every sensitive value it hands out.</summary>
internal sealed class Resolver
{
	/// <summary>Sensitive values shorter than this cannot be redacted without mangling unrelated text.</summary>
	internal const int MinimumSensitiveLength = 4;

	private readonly HttpFile _file;
	private readonly HttpEnvironment _environment;
	private readonly HttpFileRunOptions _options;
	private readonly Random _random;
	private readonly Dictionary<string, HttpFileExchange> _exchanges = [with(StringComparer.Ordinal)];
	private readonly Dictionary<string, HttpSensitiveValue> _sensitive = [with(StringComparer.Ordinal)];
	private readonly List<HttpSensitiveValue> _sensitiveInOrder = [];
	private Dictionary<string, string>? _userSecrets;
	private string? _userSecretsSource;
	private readonly Dictionary<string, Dictionary<string, string>?> _dotEnv = [with(StringComparer.Ordinal)];

	public Resolver(HttpFile file, HttpEnvironment environment, HttpFileRunOptions options)
	{
		_file = file;
		_environment = environment;
		_options = options;
		_random = options.Random ?? new Random();
	}

	public IReadOnlyList<HttpSensitiveValue> Sensitive => _sensitiveInOrder;

	public void Completed(HttpFileExchange exchange)
	{
		if (exchange.Request.Name is { } name)
			_exchanges[name] = exchange;
	}

	/// <summary>Renders <paramref name="template"/> as seen from <paramref name="scopeLine"/>: file variables defined above that line are visible.</summary>
	public string Render(Template template, int scopeLine) => template.Render(e => Resolve(e, scopeLine));

	private string Resolve(Expr expr, int scopeLine) => expr switch
	{
		VariableExpr v => ResolveVariable(v.Name, v.Line, scopeLine),
		RequestReferenceExpr r => ResolveReference(r),
		DynamicExpr d => ResolveDynamic(d, scopeLine),
		_ => throw new ResolveException(expr.Line, $"cannot resolve '{expr.Reference}'"),
	};

	private string ResolveVariable(string name, int line, int scopeLine)
	{
		if (_options.Variables.TryGetValue(name, out var overridden))
			return overridden;
		var variable = FindFileVariable(_file, name, scopeLine);
		if (variable is not null)
			return Render(variable.Template, variable.Line);
		if (_environment.Variables.TryGetValue(name, out var value))
			return FromEnvironment(value, line);
		var later = _file.Variables.FirstOrDefault(v => v.Name == name);
		if (later is not null)
			throw new ResolveException(line, $"variable '{name}' is used before its definition at line {later.Line}");
		throw new ResolveException(line, $"unknown variable '{name}'{EnvironmentHint()}");
	}

	private string EnvironmentHint() =>
		_environment.Directory is null ? " (no http-client.env.json was found)"
		: _environment.Name is null ? " (no environment is selected)"
		: $" (not in environment '{_environment.Name}')";

	internal static HttpFileVariable? FindFileVariable(HttpFile file, string name, int scopeLine) =>
		file.Variables.LastOrDefault(v => v.Name == name && v.Line < scopeLine);

	private string FromEnvironment(HttpEnvironmentValue value, int line)
	{
		var reference = "{{" + value.Name + "}}";
		if (value.Provider is null)
		{
			var plain = value.Value ?? "";
			if (value.IsSensitive)
				Record(plain, reference, line);
			return plain;
		}
		var secretName = value.SecretName ?? value.Name;
		var secret = _options.Secrets?.Invoke(secretName);
		if (secret is null && value.Provider == "AspnetUserSecrets")
			secret = FromUserSecrets(secretName, line);
		if (secret is null)
		{
			var where = value.Provider == "AspnetUserSecrets"
				? $"not in HttpFileRunOptions.Secrets nor in user secrets ({_userSecretsSource ?? "no UserSecretsId found"})"
				: "HttpFileRunOptions.Secrets did not answer it";
			throw new ResolveException(line, $"secret '{secretName}' for '{reference}' (provider {value.Provider}, {value.Source}:{value.Line}) has no value: {where}");
		}
		Record(secret, reference, line);
		return secret;
	}

	private string? FromUserSecrets(string name, int line)
	{
		if (_userSecrets is null)
		{
			var id = UserSecrets.FindId(_options, _file.Directory);
			if (id is null)
			{
				_userSecrets = [with(StringComparer.OrdinalIgnoreCase)];
				return null;
			}
			var root = _options.UserSecretsRoot ?? UserSecrets.DefaultRoot();
			_userSecretsSource = Path.Combine(root, id, "secrets.json");
			try
			{
				_userSecrets = UserSecrets.Read(root, id);
			}
			catch (JsonException e)
			{
				throw new ResolveException(line, $"user secrets file '{_userSecretsSource}' is not valid JSON: {e.Message}");
			}
		}
		return _userSecrets.TryGetValue(name, out var value) ? value : null;
	}

	/// <summary>Records a sensitive value for redaction. Too short a value is an error: replacing it everywhere would mangle text.</summary>
	private void Record(string value, string reference, int line)
	{
		if (value.Length < MinimumSensitiveLength)
			throw new ResolveException(line, $"the value of '{reference}' is shorter than {MinimumSensitiveLength} characters, too short to redact safely from the output");
		Add(value, reference);
		var escaped = Uri.EscapeDataString(value);
		if (escaped != value)
			Add(escaped, reference);
	}

	private void Add(string value, string reference)
	{
		if (_sensitive.ContainsKey(value))
			return;
		var entry = new HttpSensitiveValue(value, reference);
		_sensitive[value] = entry;
		_sensitiveInOrder.Add(entry);
	}

	private string ResolveReference(RequestReferenceExpr r)
	{
		if (!_exchanges.TryGetValue(r.RequestName, out var exchange))
		{
			var target = _file.Requests.FirstOrDefault(q => q.Name == r.RequestName);
			throw new ResolveException(r.Line, target is null
				? $"'{r.Reference}' references a request named '{r.RequestName}', and there is none"
				: $"'{r.Reference}' references request '{r.RequestName}' (line {target.Line}), which has not run yet");
		}
		IEnumerable<string> HeaderValues() => r.Response
			? exchange.Response.GetHeaderValues(r.Selector)
			: exchange.Sent.Headers.Where(h => h.Name.Equals(r.Selector, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
		if (!r.Body)
		{
			var values = HeaderValues().ToList();
			if (values.Count == 0)
				throw new ResolveException(r.Line, $"'{r.Reference}': the {(r.Response ? "response" : "request")} of '{r.RequestName}' has no '{r.Selector}' header");
			return string.Join(", ", values);
		}
		var bytes = r.Response ? exchange.Response.Body : exchange.Sent.Body ?? [];
		if (r.Selector == "*")
			return r.Response ? exchange.Response.BodyText : Encoding.UTF8.GetString(bytes);
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(bytes);
		}
		catch (JsonException)
		{
			throw new ResolveException(r.Line, $"'{r.Reference}': the {(r.Response ? "response" : "request")} body of '{r.RequestName}' is not JSON");
		}
		using (document)
		{
			if (!r.JsonPath!.TrySelect(document.RootElement, out var found))
				throw new ResolveException(r.Line, $"'{r.Reference}': '{r.Selector}' matches nothing in the {(r.Response ? "response" : "request")} body of '{r.RequestName}'");
			return JsonPath.ToText(found);
		}
	}

	private string ResolveDynamic(DynamicExpr d, int scopeLine)
	{
		var now = _options.TimeProvider.GetUtcNow();
		switch (d.Kind)
		{
			case DynamicKind.Guid:
				{
					Span<byte> bytes = stackalloc byte[16];
					_random.NextBytes(bytes);
					bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
					bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
					return new Guid(bytes).ToString("D");
				}
			case DynamicKind.RandomInt:
				return _random.Next(d.Min, d.Max).ToString(CultureInfo.InvariantCulture);
			case DynamicKind.Timestamp:
				return d.ApplyOffset(now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
			case DynamicKind.IsoTimestamp:
				return d.ApplyOffset(now).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
			case DynamicKind.DateTime:
				return FormatTime(d.ApplyOffset(now), d.Format, local: false);
			case DynamicKind.LocalDateTime:
				return FormatTime(TimeZoneInfo.ConvertTime(d.ApplyOffset(now), _options.TimeProvider.LocalTimeZone), d.Format, local: true);
			case DynamicKind.ProcessEnv:
				{
					var name = d.Indirect ? ResolveVariable(d.Argument, d.Line, scopeLine) : d.Argument;
					var value = Environment.GetEnvironmentVariable(name);
					if (string.IsNullOrEmpty(value))
						throw new ResolveException(d.Line, $"'{d.Reference}': environment variable '{name}' is not set");
					Record(value, d.Reference, d.Line);
					return value;
				}
			case DynamicKind.DotEnv:
				{
					var name = d.Indirect ? ResolveVariable(d.Argument, d.Line, scopeLine) : d.Argument;
					var value = FromDotEnv(name) ?? throw new ResolveException(d.Line, $"'{d.Reference}': '{name}' is not defined in a .env file ({string.Join(", ", DotEnv.SearchFolders(_file.Directory))})");
					Record(value, d.Reference, d.Line);
					return value;
				}
			case DynamicKind.Secret:
				{
					var value = (_options.Secrets?.Invoke(d.Argument)) ?? throw new ResolveException(d.Line, $"'{d.Reference}' has no value: HttpFileRunOptions.Secrets did not answer '{d.Argument}'");
					Record(value, d.Reference, d.Line);
					return value;
				}
			default:
				throw new ResolveException(d.Line, $"cannot resolve '{d.Reference}'");
		}
	}

	private static string FormatTime(DateTimeOffset time, string? format, bool local) => format switch
	{
		null or "iso8601" => time.ToString("o", CultureInfo.InvariantCulture),
		// Visual Studio writes local RFC 1123 times with the offset in hours ("-08"), UTC ones with "GMT".
		"rfc1123" when local => time.ToString("ddd, dd MMM yyyy HH:mm:ss zz", CultureInfo.InvariantCulture),
		"rfc1123" => time.ToString("r", CultureInfo.InvariantCulture),
		_ => time.ToString(format, CultureInfo.InvariantCulture),
	};

	/// <summary>The value from the nearest <c>.env</c> file that defines <paramref name="name"/>.</summary>
	private string? FromDotEnv(string name)
	{
		foreach (var folder in DotEnv.SearchFolders(_file.Directory))
		{
			if (!_dotEnv.TryGetValue(folder, out var values))
			{
				var path = Path.Combine(folder, ".env");
				values = File.Exists(path) ? DotEnv.Parse(File.ReadAllText(path)) : null;
				_dotEnv[folder] = values;
			}
			if (values is not null && values.TryGetValue(name, out var value))
				return value;
		}
		return null;
	}
}
