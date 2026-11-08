using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using CommonUtilities;

namespace RestApiMCPClient;

// ============================================
// PROVIDED - do not edit
// ============================================

/// <summary>
/// Console output helpers shared by the scenarios, so that Program.cs focuses on the MCP client and the agent.
/// </summary>
public static class AgentConsole
{
    /// <summary>The tools of the remote MCP server, as the agent receives them (name and description).</summary>
    public static void WriteTools(string clientId, IList<McpClientTool> tools)
    {
        ColoredConsole.WritePrimaryLogLine($"MCP tools for {clientId} ({tools.Count}): {string.Join(", ", tools.Select(tool => tool.Name).Order())}");
    }

    /// <summary>
    /// The tool calls of a run: each FunctionCallContent (asked by the model, run by this application through its MCP client)
    /// with its FunctionResultContent (what the remote MCP server returned).
    /// </summary>
    public static void WriteToolCalls(AgentResponse response)
    {
        List<AIContent> contents = [.. response.Messages.SelectMany(message => message.Contents)];
        List<FunctionCallContent> calls = [.. contents.OfType<FunctionCallContent>()];
        if (calls.Count == 0)
        {
            ColoredConsole.WriteWarningLine("Tool calls: none");
            return;
        }

        foreach (FunctionCallContent call in calls)
        {
            string arguments = string.Join(", ", call.Arguments?.Select(argument => $"{argument.Key}: {Format(argument.Value)}") ?? []);
            ColoredConsole.WritePrimaryLogLine($"Tool called: {call.Name}({arguments})");
            FunctionResultContent? result = contents.OfType<FunctionResultContent>().FirstOrDefault(r => r.CallId == call.CallId);
            ColoredConsole.WriteSecondaryLogLine($"  Result: {Shorten(Format(result?.Result), 300)}");
        }
    }

    /// <summary>The answer of the agent.</summary>
    public static void WriteAnswer(AgentResponse response)
    {
        ColoredConsole.WritePrimaryLogLine("Agent answer:");
        ColoredConsole.WriteSecondaryLogLine(response.Text);
    }

    /// <summary>The token usage of the run: the model calls of this agent only (the MCP server has no model).</summary>
    public static void WriteTokenUsage(AgentResponse response)
    {
        ColoredConsole.WriteDividerLine();
        ColoredConsole.WritePrimaryLogLine("Token Usage:");
        ColoredConsole.WriteSecondaryLogLine($"  Input tokens: {response.Usage?.InputTokenCount}");
        ColoredConsole.WriteSecondaryLogLine($"  Output tokens: {response.Usage?.OutputTokenCount}");
        ColoredConsole.WriteSecondaryLogLine($"  Total tokens: {response.Usage?.TotalTokenCount}");
    }

    /// <summary>The result of a direct tools/call (without the agent).</summary>
    public static void WriteToolResult(string call, CallToolResult result)
    {
        string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (result.IsError == true)
        {
            ColoredConsole.WriteWarningLine($"{call} -> tool error: {text}");
        }
        else
        {
            ColoredConsole.WritePrimaryLogLine($"{call} -> {Shorten(text, 300)}");
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "(none)",
        CallToolResult result => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)) + (result.IsError == true ? " (tool error)" : ""),
        TextContent text => text.Text,
        JsonElement json => FormatJson(json),
        _ => value.ToString() ?? ""
    };

    // The McpClientTool gives the model the result of tools/call as JSON: its text content, and "isError" for a tool error
    private static string FormatJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return json.GetRawText();
        }

        string text = string.Join(" ", content.EnumerateArray()
            .Where(block => block.TryGetProperty("text", out _))
            .Select(block => block.GetProperty("text").GetString()));
        bool isError = json.TryGetProperty("isError", out JsonElement error) && error.ValueKind == JsonValueKind.True;
        return isError ? $"{text} (tool error)" : text;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
