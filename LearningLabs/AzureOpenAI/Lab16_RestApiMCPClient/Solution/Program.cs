using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using OpenAI;
using OpenAI.Chat;
using CommonUtilities;

using RestApiMCPClient;
using static RestApiMCPClient.AgentConsole;

// ============================================
// SCENARIO SELECTION - Choose which scenarios to run
// ============================================
// dotnet run                          -> the implemented scenarios (a scenario whose code is still the delivered one is left out)
// dotnet run -- --scenarios 2         -> scenario 2 only (and the scenarios it requires)
// dotnet run -- --scenarios 1,3       -> scenarios 1 and 3
// dotnet run -- --list-scenarios      -> the scenarios of this lab and their state
// All scenarios need the MCP server of Lab16_RestApiMCPServer, started with: dotnet run -- --serve
ScenarioSelection scenarioSelection = ScenarioSelection.FromArgs(args);
// ============================================

bool ShouldRunScenario(int scenario) => scenarioSelection.ShouldRun(scenario);

#region Setup: Configuration, Azure OpenAI client and the remote MCP server

// This program is the agent of the CONSUMER team. It does not know the REST microservice of the provider team:
// only the URL of its remote MCP server and the client credentials the provider team gave.

// Step 1: Load Azure OpenAI settings from configuration
var settings = ConfigurationHelper.GetAzureOpenAISettings();
Console.WriteLine($"Endpoint: {settings.Endpoint}");
Console.WriteLine($"Deployment: {settings.ChatDeploymentName}");

// Step 2: Create an OpenAIClient that targets the Azure OpenAI v1 endpoint (https://<resource>.openai.azure.com/openai/v1/)
// - API key when one is configured
// - otherwise Microsoft Entra ID: DefaultAzureCredential + BearerTokenPolicy (scope https://ai.azure.com/.default)
// WARNING: DefaultAzureCredential is convenient for development. In production, prefer a specific credential
// (e.g. ManagedIdentityCredential) to avoid latency and unintended credential probing.
OpenAIClientOptions clientOptions = new() { Endpoint = AzureOpenAIEndpoint.ToV1Uri(settings.Endpoint) };

// The OpenAIClient(AuthenticationPolicy, ...) constructor is still flagged [Experimental] (OPENAI001) by the OpenAI SDK,
// although it is the pattern documented by Microsoft for Entra ID with the Azure OpenAI v1 API.
#pragma warning disable OPENAI001
OpenAIClient client = !string.IsNullOrWhiteSpace(settings.APIKey)
    ? new OpenAIClient(new ApiKeyCredential(settings.APIKey), clientOptions)
    : new OpenAIClient(new BearerTokenPolicy(new DefaultAzureCredential(), "https://ai.azure.com/.default"), clientOptions);
#pragma warning restore OPENAI001

// Step 3: Get a ChatClient (Chat Completions API) for the deployment
ChatClient chatClient = client.GetChatClient(settings.ChatDeploymentName);

// Step 4: Load the settings of the remote MCP server: its URL, its token endpoint and the client credentials
RemoteMcpServerSettings remoteServer = ConfigurationHelper.GetRemoteMcpServerSettings();
Console.WriteLine($"Remote MCP server: {remoteServer.Url}");

using HttpClient http = new();

// Solution of TODO 4: A function that connects an MCP client to the remote server for one client of the consumer team:
// - get an access token with the client credentials grant (DevTokenClient.cs), for the resource = the MCP endpoint
// - connect with the Streamable HTTP transport, the token in the Authorization header of every request
async Task<McpClient> ConnectAsync(ClientCredentials credentials)
{
    string accessToken = await DevTokenClient.GetAccessTokenAsync(
        http, remoteServer.TokenEndpoint, credentials.ClientId, credentials.ClientSecret, resource: remoteServer.Url);

    return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = credentials.ClientId,
        Endpoint = new Uri(remoteServer.Url),
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {accessToken}" }
    }));
}

#endregion

#region Scenario 1: Give the tools of the remote MCP server to an agent

if (ShouldRunScenario(1))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 1: Give the tools of the remote MCP server to an agent ===");

    // Step 1: Connect as the sales-agent client and list the tools the provider team exposes to it
    await using McpClient mcpClient = await ConnectAsync(remoteServer.Clients["SalesAgent"]);
    IList<McpClientTool> tools = await mcpClient.ListToolsAsync();
    WriteTools("sales-agent", tools);

    // Step 2: Create the SalesAssistant with the MCP tools: each McpClientTool is an AIFunction, its calls go to the remote server
    AIAgent salesAssistant = chatClient.AsAIAgent(
        instructions: Agents.SalesAssistant.Instructions,
        name: Agents.SalesAssistant.Name,
        tools: [.. tools.Cast<AITool>()]);

    // Step 3: Ask a question that needs two tools, then display the tool calls, the answer and the token usage
    ColoredConsole.WriteUserLine($"User: {Questions.KeyboardsAndOrder}");
    AgentResponse response = await salesAssistant.RunAsync(Questions.KeyboardsAndOrder).WithSpinner("Running the SalesAssistant");
    WriteToolCalls(response);
    WriteAnswer(response);
    WriteTokenUsage(response);
}

#endregion

#region Scenario 2: Let the agent place an order and handle the errors of the REST API

if (ShouldRunScenario(2))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 2: Let the agent place an order and handle the errors of the REST API ===");

    await using McpClient mcpClient = await ConnectAsync(remoteServer.Clients["SalesAgent"]);
    IList<McpClientTool> tools = await mcpClient.ListToolsAsync();
    AIAgent salesAssistant = chatClient.AsAIAgent(
        instructions: Agents.SalesAssistant.Instructions,
        name: Agents.SalesAssistant.Name,
        tools: [.. tools.Cast<AITool>()]);

    // Step 1: An order larger than the stock: the REST API answers 409, the MCP server returns a tool error with the stock
    // left, and the model adapts its call (a second place_order with the quantity available)
    ColoredConsole.WriteUserLine($"User: {Questions.OrderTenKeyboards}");
    AgentResponse order = await salesAssistant.RunAsync(Questions.OrderTenKeyboards).WithSpinner("Running the SalesAssistant");
    WriteToolCalls(order);
    WriteAnswer(order);

    // Step 2: An unknown order: the REST API answers 404, the agent gets the message of the microservice and says so
    ColoredConsole.WriteUserLine($"User: {Questions.UnknownOrder}");
    AgentResponse unknown = await salesAssistant.RunAsync(Questions.UnknownOrder).WithSpinner("Running the SalesAssistant");
    WriteToolCalls(unknown);
    WriteAnswer(unknown);
    WriteTokenUsage(unknown);
}

#endregion

#region Scenario 3: A read-only consumer cannot order, whatever its agent decides

if (ShouldRunScenario(3))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 3: A read-only consumer cannot order, whatever its agent decides ===");

    // Step 1: Connect as the reporting-agent client (scope orders.read): the server does not list place_order for it
    await using McpClient mcpClient = await ConnectAsync(remoteServer.Clients["ReportingAgent"]);
    IList<McpClientTool> tools = await mcpClient.ListToolsAsync();
    WriteTools("reporting-agent", tools);

    // Step 2: The ReportingAssistant answers the read part and says it cannot order: it has no tool for it
    AIAgent reportingAssistant = chatClient.AsAIAgent(
        instructions: Agents.ReportingAssistant.Instructions,
        name: Agents.ReportingAssistant.Name,
        tools: [.. tools.Cast<AITool>()]);
    ColoredConsole.WriteUserLine($"User: {Questions.StockAndOrder}");
    AgentResponse response = await reportingAssistant.RunAsync(Questions.StockAndOrder).WithSpinner("Running the ReportingAssistant");
    WriteToolCalls(response);
    WriteAnswer(response);

    // Step 3: Even a client that calls place_order directly (a bug, a prompt injection, another program) is refused by the server
    try
    {
        WriteToolResult("Direct call of place_order as reporting-agent", await mcpClient.CallToolAsync("place_order", new Dictionary<string, object?>
        {
            ["customerId"] = "CUST-077",
            ["lines"] = new[] { new { sku = "MN-270", quantity = 1 } }
        }));
    }
    catch (McpException ex)
    {
        ColoredConsole.WriteWarningLine($"Direct call of place_order as reporting-agent -> refused by the server: {ex.Message}");
    }

    WriteTokenUsage(response);
}

#endregion
