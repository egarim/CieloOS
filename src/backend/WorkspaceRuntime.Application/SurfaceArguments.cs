using System.Text.Json;

namespace WorkspaceRuntime.Application;

// JSON input -> the string map the command bus takes.
//
// One definition, because there are now two ways in: the surface-command endpoint
// a person's panel uses, and the MCP server an engine uses. Two copies of this
// would be two readings of what "absent" means, on the path where absent decides
// whether required-input validation fires.
public static class SurfaceArguments
{
    public static Dictionary<string, string> From(Dictionary<string, JsonElement>? input)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input is null)
        {
            return arguments;
        }

        foreach (var pair in input)
        {
            // JSON null/undefined values are treated as absent, not as the literal
            // string "null" — the required-input validation catches them.
            if (pair.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            arguments[pair.Key] = pair.Value.ValueKind == JsonValueKind.String
                ? pair.Value.GetString() ?? ""
                : pair.Value.GetRawText();
        }

        return arguments;
    }
}
