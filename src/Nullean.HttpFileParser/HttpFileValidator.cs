namespace Nullean.HttpFileParser;

/// <summary>The static checks behind <see cref="HttpFile.Validate"/>.</summary>
internal static class HttpFileValidator
{
	public static IReadOnlyList<HttpFileError> Validate(HttpFile file, HttpEnvironment? environment, IReadOnlyDictionary<string, string>? variables)
	{
		environment ??= HttpEnvironment.Empty;
		var errors = new List<HttpFileError>();
		var seen = new HashSet<(int, string)>();
		void Error(int line, string message)
		{
			if (seen.Add((line, message)))
				errors.Add(new HttpFileError(file.Path, line, message));
		}

		var checkedVariables = new HashSet<(HttpFileVariable, int)>();
		void Check(Template template, int scopeLine, int requestIndex)
		{
			foreach (var expr in template.Expressions)
			{
				switch (expr)
				{
					case VariableExpr v:
						CheckVariable(v.Name, v.Line, scopeLine, requestIndex);
						break;
					case DynamicExpr { Indirect: true } d:
						CheckVariable(d.Argument, d.Line, scopeLine, requestIndex);
						break;
					case RequestReferenceExpr r:
						var earlier = file.Requests.Take(requestIndex).Any(q => q.Name == r.RequestName);
						if (!earlier)
						{
							var target = file.Requests.FirstOrDefault(q => q.Name == r.RequestName);
							Error(r.Line, target is null
								? $"'{r.Reference}' references a request named '{r.RequestName}', and there is none"
								: $"'{r.Reference}' references request '{r.RequestName}' (line {target.Line}), which runs after this one");
						}
						break;
					default:
						break;
				}
			}
		}

		void CheckVariable(string name, int line, int scopeLine, int requestIndex)
		{
			if (variables?.ContainsKey(name) == true)
				return;
			var variable = Resolver.FindFileVariable(file, name, scopeLine);
			if (variable is not null)
			{
				if (checkedVariables.Add((variable, requestIndex)))
					Check(variable.Template, variable.Line, requestIndex);
				return;
			}
			if (environment.Variables.ContainsKey(name))
				return;
			var later = file.Variables.FirstOrDefault(v => v.Name == name);
			Error(line, later is not null
				? $"variable '{name}' is used before its definition at line {later.Line}"
				: $"unknown variable '{name}'");
		}

		foreach (var request in file.Requests)
		{
			Check(request.Url, request.Line, request.Index);
			foreach (var header in request.Headers)
				Check(header.Template, request.Line, request.Index);
			if (request.Body is not { } body)
				continue;
			foreach (var part in body.Parts)
			{
				if (part.Template is not null)
					Check(part.Template, request.Line, request.Index);
				else if (!File.Exists(part.FullPath))
					Error(part.Line, $"body file '{part.FilePath}' does not exist ({part.FullPath})");
			}
		}
		// Variables no request uses are still checked, so a broken definition does not hide until it is used. A response
		// reference in a variable resolves where the variable is used, so here any request of the file counts.
		foreach (var variable in file.Variables)
		{
			if (!checkedVariables.Any(c => c.Item1 == variable))
				Check(variable.Template, variable.Line, file.Requests.Count);
		}
		return [.. errors.OrderBy(e => e.Line)];
	}
}
