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

// TODO 1: Create the OpenAIClientOptions that point to the Azure OpenAI v1 endpoint
// Hint: AzureOpenAIEndpoint.ToV1Uri(settings.Endpoint) turns "https://<resource>.openai.azure.com/" into ".../openai/v1/"
// OpenAIClientOptions clientOptions = ...

// TODO 2: Create the OpenAIClient
// - If settings.APIKey is set: authenticate with an ApiKeyCredential
// - Otherwise: authenticate with Microsoft Entra ID using a BearerTokenPolicy built from
//   DefaultAzureCredential and the scope "https://ai.azure.com/.default"
// Note: the AuthenticationPolicy constructor is flagged experimental (OPENAI001), keep the pragma around it.
#pragma warning disable OPENAI001
// OpenAIClient client = ...
#pragma warning restore OPENAI001

// TODO 3: Get a ChatClient (Chat Completions API) for the deployment
// ChatClient chatClient = ...

// Step 4: Load the settings of the remote MCP server: its URL, its token endpoint and the client credentials
RemoteMcpServerSettings remoteServer = ConfigurationHelper.GetRemoteMcpServerSettings();
Console.WriteLine($"Remote MCP server: {remoteServer.Url}");

using HttpClient http = new();

// TODO 4: Write a local function that connects an MCP client to the remote server for one client of the consumer team:
// - get an access token with the client credentials grant (DevTokenClient.cs), for the resource = the MCP endpoint (remoteServer.Url)
// - connect with the Streamable HTTP transport, the token in the Authorization header of every request
// Hint: async Task<McpClient> ConnectAsync(ClientCredentials credentials)
//       {
//           string accessToken = await DevTokenClient.GetAccessTokenAsync(http, remoteServer.TokenEndpoint, credentials.ClientId,
//               credentials.ClientSecret, resource: remoteServer.Url);
//           return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Name = credentials.ClientId,
//               Endpoint = new Uri(remoteServer.Url), TransportMode = HttpTransportMode.StreamableHttp,
//               AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {accessToken}" } }));
//       }

#endregion

#region Scenario 1: Give the tools of the remote MCP server to an agent

if (ShouldRunScenario(1))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 1: Give the tools of the remote MCP server to an agent ===");

    // TODO 5: Connect as the sales-agent client (remoteServer.Clients["SalesAgent"]), list the tools and display them
    // Hint: await using McpClient mcpClient = await ConnectAsync(remoteServer.Clients["SalesAgent"]);
    //       IList<McpClientTool> tools = await mcpClient.ListToolsAsync(); WriteTools("sales-agent", tools);

    // TODO 6: Create the SalesAssistant (Agents.SalesAssistant in LabPrompts.cs) with the MCP tools:
    // each McpClientTool is an AIFunction, its calls go to the remote server
    // Hint: chatClient.AsAIAgent(instructions: Agents.SalesAssistant.Instructions, name: Agents.SalesAssistant.Name, tools: [.. tools.Cast<AITool>()])
    // AIAgent salesAssistant = ...

    // TODO 7: Ask Questions.KeyboardsAndOrder, then display the tool calls, the answer and the token usage
    // Hint: ColoredConsole.WriteUserLine($"User: {Questions.KeyboardsAndOrder}");
    //       AgentResponse response = await salesAssistant.RunAsync(Questions.KeyboardsAndOrder).WithSpinner("Running the SalesAssistant");
    //       WriteToolCalls(response); WriteAnswer(response); WriteTokenUsage(response);
}

#endregion

#region Scenario 2: Let the agent place an order and handle the errors of the REST API

if (ShouldRunScenario(2))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 2: Let the agent place an order and handle the errors of the REST API ===");

    // TODO 8: Connect as sales-agent and create the SalesAssistant with the MCP tools (same as TODO 5 and TODO 6)

    // TODO 9: Ask Questions.OrderTenKeyboards: the REST API answers 409 (not enough stock), the MCP server returns a tool error with
    // the stock left, and the model calls place_order again with the quantity available. Display the tool calls and the answer
    // Hint: ColoredConsole.WriteUserLine(...); RunAsync(...).WithSpinner("Running the SalesAssistant"); WriteToolCalls(order); WriteAnswer(order);

    // TODO 10: Ask Questions.UnknownOrder (404): display the tool calls, the answer and the token usage
}

#endregion

#region Scenario 3: A read-only consumer cannot order, whatever its agent decides

if (ShouldRunScenario(3))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 3: A read-only consumer cannot order, whatever its agent decides ===");

    // TODO 11: Connect as the reporting-agent client (remoteServer.Clients["ReportingAgent"], scope orders.read),
    // list its tools and display them with WriteTools("reporting-agent", tools): the server does not list place_order for it

    // TODO 12: Create the ReportingAssistant (Agents.ReportingAssistant) with these tools, ask Questions.StockAndOrder,
    // and display the tool calls and the answer: it answers the stock and says it cannot order

    // TODO 13: Call place_order directly (as a bug, a prompt injection or another program would) and display the refusal
    // Hint: try { WriteToolResult("Direct call of place_order as reporting-agent", await mcpClient.CallToolAsync("place_order",
    //           new Dictionary<string, object?> { ["customerId"] = "CUST-077", ["lines"] = new[] { new { sku = "MN-270", quantity = 1 } } })); }
    //       catch (McpException ex) { ColoredConsole.WriteWarningLine($"Direct call of place_order as reporting-agent -> refused by the server: {ex.Message}"); }

    // TODO 14: Display the token usage of the ReportingAssistant
}

#endregion
