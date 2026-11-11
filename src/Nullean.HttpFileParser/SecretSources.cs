using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Nullean.HttpFileParser;

/// <summary>.NET user secrets (<c>secrets.json</c>), read without referencing Microsoft.Extensions.Configuration.</summary>
internal static class UserSecrets
{
	private const string AttributeName = "Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute";

	/// <summary>The id from the options, else the assembly attribute, else <c>&lt;UserSecretsId&gt;</c> in the nearest project file above <paramref name="httpFileDirectory"/>.</summary>
	public static string? FindId(HttpFileRunOptions options, string httpFileDirectory)
	{
		if (!string.IsNullOrEmpty(options.UserSecretsId))
			return options.UserSecretsId;
		if (options.SecretsAssembly is { } assembly && FromAssembly(assembly) is { } fromAssembly)
			return fromAssembly;
		var project = FindProjectFile(httpFileDirectory);
		return project is null ? null : FromProject(project);
	}

	internal static string? FromAssembly(Assembly assembly)
	{
		foreach (var attribute in assembly.GetCustomAttributesData())
		{
			if (
				attribute.AttributeType.FullName == AttributeName && attribute.ConstructorArguments.Count == 1 &&
				attribute.ConstructorArguments[0].Value is string id
			)
				return id;
		}
		return null;
	}

	internal static string? FromProject(string projectFile)
	{
		try
		{
			var document = XDocument.Load(projectFile);
			return document.Descendants().FirstOrDefault(e => e.Name.LocalName == "UserSecretsId")?.Value.Trim() is { Length: > 0 } id ? id : null;
		}
		catch (System.Xml.XmlException)
		{
			return null;
		}
	}

	/// <summary>The nearest <c>*.csproj</c> in <paramref name="directory"/> or above it.</summary>
	internal static string? FindProjectFile(string directory)
	{
		for (var dir = directory; dir is not null; dir = Path.GetDirectoryName(dir))
		{
			if (!Directory.Exists(dir))
				continue;
			var project = Directory.EnumerateFiles(dir, "*.csproj").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
			if (project is not null)
				return project;
		}
		return null;
	}

	/// <summary>Where <c>dotnet user-secrets</c> keeps secrets: <c>%APPDATA%\Microsoft\UserSecrets</c> on Windows, <c>~/.microsoft/usersecrets</c> elsewhere.</summary>
	public static string DefaultRoot() =>
		OperatingSystem.IsWindows()
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");

	/// <summary>Reads <c>{root}/{id}/secrets.json</c>, flattened to <c>Section:Key</c> like Microsoft configuration does. Missing file: empty.</summary>
	public static Dictionary<string, string> Read(string root, string id)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var path = Path.Combine(root, id, "secrets.json");
		if (!File.Exists(path))
			return result;
		using var document = JsonDocument.Parse(
			File.ReadAllBytes(path),
			new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }
		);
		Flatten(document.RootElement, null, result);
		return result;
	}

	private static void Flatten(JsonElement element, string? prefix, Dictionary<string, string> into)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var p in element.EnumerateObject())
					Flatten(p.Value, prefix is null ? p.Name : prefix + ":" + p.Name, into);
				break;
			case JsonValueKind.Array:
				var i = 0;
				foreach (var item in element.EnumerateArray())
					Flatten(item, (prefix is null ? "" : prefix + ":") + i++.ToString(System.Globalization.CultureInfo.InvariantCulture), into);
				break;
			case JsonValueKind.Null:
				if (prefix is not null)
					into[prefix] = "";
				break;
			default:
				if (prefix is not null)
					into[prefix] = element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
				break;
		}
	}
}

/// <summary><c>.env</c> files for <c>{{$dotenv NAME}}</c>.</summary>
internal static class DotEnv
{
	/// <summary>
	/// The folders searched: the <c>.http</c> file's folder and each folder above it up to and including the nearest one
	/// with a <c>*.csproj</c>; only the file's own folder when no project is found.
	/// </summary>
	public static IReadOnlyList<string> SearchFolders(string httpFileDirectory)
	{
		var project = UserSecrets.FindProjectFile(httpFileDirectory);
		var stop = project is null ? httpFileDirectory : Path.GetDirectoryName(project)!;
		var folders = new List<string>();
		for (var dir = httpFileDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
		{
			folders.Add(dir);
			if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), stop.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
				break;
		}
		return folders;
	}

	/// <summary>Parses <c>KEY=value</c> lines; <c>#</c> comments, <c>export </c> prefixes and surrounding quotes are allowed.</summary>
	public static Dictionary<string, string> Parse(string text)
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#'))
				continue;
			if (line.StartsWith("export ", StringComparison.Ordinal))
				line = line[7..].TrimStart();
			var eq = line.IndexOf('=', StringComparison.Ordinal);
			if (eq <= 0)
				continue;
			var key = line[..eq].Trim();
			var value = line[(eq + 1)..].Trim();
			if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
				value = value[1..^1];
			values[key] = value;
		}
		return values;
	}
}
