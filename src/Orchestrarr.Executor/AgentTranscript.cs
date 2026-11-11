using System.Text.Json;

namespace Orchestrarr.Executor;

/// <summary>
/// Turns the lines of the stream of Claude Code (<c>--output-format stream-json</c>) into lines a person reads in the trace: what the agent says, what it
/// reads, edits and runs, and what it got back. Only what the agent did is kept; the session, the model and the cost are not (a cost of the
/// subscription is not a bill). What a tool returned is cut: a file read is not copied into the trace.
/// </summary>
public static class AgentTranscript
{
    private const int Said = 1200;

    private const int Returned = 300;

    private const int Changed = 500;

    /// <summary>The lines of the trace for one line of the stream; none for a line that tells nothing (or that is not understood).</summary>
    public static IReadOnlyList<string> Describe(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (type is not ("assistant" or "user") || !root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return [.. content.EnumerateArray().Select(Block).Where(text => text is not null).Select(text => text!)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The final answer and the number of turns, from the last line of the stream; null when this is not that line.</summary>
    public static (string Text, string Turns, bool IsError)? Result(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "result")
            {
                var text = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? string.Empty : string.Empty;
                var turns = root.TryGetProperty("num_turns", out var count) ? count.ToString() : "?";
                return (text, turns, root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True);
            }
        }
        catch (JsonException)
        {
            // Not a line of the stream
        }
        return null;
    }

    private static string? Block(JsonElement block)
    {
        var kind = block.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        return kind switch
        {
            "text" => Text("says", block, "text", Said),
            "thinking" => Text("thinks", block, "thinking", Said),
            "tool_use" => ToolUse(block),
            "tool_result" => ToolResult(block),
            _ => null,
        };
    }

    private static string? Text(string verb, JsonElement block, string property, int limit)
    {
        var text = block.TryGetProperty(property, out var value) ? value.GetString()?.Trim() : null;
        return string.IsNullOrEmpty(text) ? null : $"{verb}: {Cut(text, limit)}";
    }

    private static string ToolUse(JsonElement block)
    {
        var name = block.TryGetProperty("name", out var value) ? value.GetString() ?? "a tool" : "a tool";
        var input = block.TryGetProperty("input", out var inputValue) && inputValue.ValueKind == JsonValueKind.Object ? inputValue : default;
        string Field(string field) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(field, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() ?? string.Empty : string.Empty;
        var path = Path(Field("file_path"));
        return name switch
        {
            "Read" => $"reads {path}",
            "Edit" or "MultiEdit" => $"edits {path}\n- {Cut(Field("old_string"), Changed)}\n+ {Cut(Field("new_string"), Changed)}",
            "Write" => $"writes {path}",
            "Glob" => $"looks for files {Field("pattern")}",
            "Grep" => $"searches {Field("pattern")}",
            "Bash" => $"runs {Cut(Field("command"), Changed)}",
            _ => $"uses {name}",
        };
    }

    private static string? ToolResult(JsonElement block)
    {
        var error = block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
        var text = block.TryGetProperty("content", out var content) ? ResultText(content) : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var verb = error ? "got an error" : "got";
        return $"{verb}: {Cut(text.Trim(), Returned)}";
    }

    private static string? ResultText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString(),
        JsonValueKind.Array => string.Join('\n', content.EnumerateArray().Select(part => part.TryGetProperty("text", out var inner) ? inner.GetString() : null).Where(part => part is not null)),
        _ => null,
    };

    // The folder of the run is /work in the container: what a person reads is the path in the repository
    private static string Path(string path) => path.StartsWith("/work/", StringComparison.Ordinal) ? path["/work/".Length..] : path;

    private static string Cut(string text, int limit) => text.Length > limit ? text[..limit] + "…" : text;
}
