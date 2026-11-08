using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using CommonUtilities;

namespace RestApiMCPServer;

// ============================================
// PROVIDED - do not edit
// ============================================

/// <summary>
/// Console output helpers of the scenarios, so that Program.cs focuses on the MCP server.
/// They show what an MCP client (and the model of an agent behind it) receives from the server.
/// </summary>
public static class McpConsole
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = false };

    /// <summary>The tools a client sees in tools/list: name, title, annotations, description, input schema and output schema.</summary>
    public static void WriteTools(IList<McpClientTool> tools, bool withSchemas = true)
    {
        ColoredConsole.WritePrimaryLogLine($"MCP tools available ({tools.Count}): {string.Join(", ", tools.Select(tool => tool.Name).Order())}");
        if (!withSchemas)
        {
            return;
        }

        foreach (McpClientTool tool in tools.OrderBy(tool => tool.Name))
        {
            Tool protocolTool = tool.ProtocolTool;
            ToolAnnotations? annotations = protocolTool.Annotations;
            string hints = annotations is null ? "none" : string.Join(", ",
                new[]
                {
                    annotations.ReadOnlyHint == true ? "read-only" : null,
                    annotations.DestructiveHint == true ? "destructive" : null,
                    annotations.IdempotentHint == true ? "idempotent" : null,
                    annotations.OpenWorldHint == false ? "closed world" : null
                }.Where(hint => hint is not null));
            ColoredConsole.WriteSecondaryLogLine($"- {tool.Name} ({protocolTool.Title ?? annotations?.Title}) [{hints}]");
            ColoredConsole.WriteSecondaryLogLine($"  Description: {tool.Description}");
            ColoredConsole.WriteSecondaryLogLine($"  Input schema: {protocolTool.InputSchema.GetRawText()}");
            ColoredConsole.WriteSecondaryLogLine($"  Output schema: {(protocolTool.OutputSchema is { } output ? Shorten(output.GetRawText(), 220) : "(none)")}");
        }
    }

    /// <summary>The result of a tools/call request: an error, or the content (text) and the structured content.</summary>
    public static void WriteToolResult(string call, CallToolResult result)
    {
        if (result.IsError == true)
        {
            ColoredConsole.WriteWarningLine($"{call} -> tool error (isError: true):");
            foreach (TextContentBlock text in result.Content.OfType<TextContentBlock>())
            {
                ColoredConsole.WriteSecondaryLogLine($"  {text.Text}");
            }

            return;
        }

        ColoredConsole.WritePrimaryLogLine($"{call} -> result:");
        if (result.StructuredContent is { } structured)
        {
            ColoredConsole.WriteSecondaryLogLine($"  Structured content: {Shorten(JsonSerializer.Serialize(structured, Indented), 600)}");
        }
        else
        {
            foreach (TextContentBlock text in result.Content.OfType<TextContentBlock>())
            {
                ColoredConsole.WriteSecondaryLogLine($"  {Shorten(text.Text, 600)}");
            }
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
