using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using CommonUtilities;

using RestApiMCPServer;
using RestApiMCPServer.Auth;
using RestApiMCPServer.Hosting;
using RestApiMCPServer.RestClient;
using RestApiMCPServer.Tools;
using static RestApiMCPServer.McpConsole;

// ============================================
// SCENARIO SELECTION - Choose which scenarios to run
// ============================================
// dotnet run                          -> the implemented scenarios (a scenario whose code is still the delivered one is left out)
// dotnet run -- --scenarios 2         -> scenario 2 only (and the scenarios it requires)
// dotnet run -- --scenarios 1,3       -> scenarios 1 and 3
// dotnet run -- --list-scenarios      -> the scenarios of this lab and their state
// dotnet run -- --serve               -> no scenario: serve MCP clients (Lab16_RestApiMCPClient, MCP Inspector, VS Code) until Ctrl+C
ScenarioSelection scenarioSelection = ScenarioSelection.FromArgs(args);
// ============================================

bool ShouldRunScenario(int scenario) => scenarioSelection.ShouldRun(scenario);

#region Setup: configuration and the REST microservice

// This program is the MCP server of the PROVIDER team. It has no agent and no model: no Azure OpenAI configuration is needed.

// Step 1: Load the settings: where the REST microservice is and the API key of the MCP server (OrdersApi),
// and where the MCP server listens (McpServer)
OrdersApiSettings ordersApiSettings = ConfigurationHelper.GetOrdersApiSettings();
McpServerSettings serverSettings = ConfigurationHelper.GetMcpServerSettings();
bool serveOnly = serverSettings.Serve || args.Contains("--serve");

// The MCP endpoint: the URL the MCP clients connect to. It is also the identifier of the protected resource (OAuth audience).
const string McpPath = "/mcp";
string mcpUrl = $"{serverSettings.BaseUrl}{McpPath}";

// Provided: make sure the REST microservice runs, as a SEPARATE process (Hosting/OrdersApiProcess.cs).
// The MCP server only knows its URL and its API key, as in production.
await using OrdersApiProcess ordersApiProcess = await OrdersApiProcess.EnsureRunningAsync(ordersApiSettings);

// Provided: the development authorization server (Auth/DevAuthorizationServer.cs). It stands in for Microsoft Entra ID:
// it issues tokens to the registered clients (DevAuthorization:Clients) for this MCP server (audience = mcpUrl)
DevAuthorizationServer authorizationServer = new(serverSettings.BaseUrl, mcpUrl, ConfigurationHelper.GetDevClients());

#endregion

#region The MCP server: selected REST endpoints exposed as MCP tools, protected, deployable

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

// TODO 5: Connect the MCP server to the REST API: register the typed HttpClient of OrdersApi (RestClient/OrdersApiClient.cs)
// - the address of the microservice: ordersApiSettings.BaseUrl (+ "/")
// - the API key OF THE MCP SERVER in the X-Api-Key header: its own identity, never the token of an MCP client (no token passthrough)
// - a timeout: ordersApiSettings.TimeoutSeconds (a slow API must not block the agents)
// OrdersApiClient also reads the client_id of the caller (for the X-On-Behalf-Of audit header): register IHttpContextAccessor
// Hint: builder.Services.AddHttpContextAccessor();
//       builder.Services.AddHttpClient<OrdersApiClient>(http => { http.BaseAddress = new Uri(ordersApiSettings.BaseUrl + "/");
//           http.DefaultRequestHeaders.Add("X-Api-Key", ordersApiSettings.ApiKey); http.Timeout = TimeSpan.FromSeconds(ordersApiSettings.TimeoutSeconds); });

// TODO 6: Register the MCP server
// - its identity: AddMcpServer(options => options.ServerInfo = new Implementation { Name = "orders-mcp-server", Title = "Orders MCP server", Version = "1.0.0" })
// - the Streamable HTTP transport, stateless (every request stands alone: several instances can run behind a load balancer):
//   .WithHttpTransport(options => options.Stateless = true)
// - the tools you wrote: .WithTools<CatalogTools>().WithTools<OrderTools>() - 4 tools for the 8 REST endpoints
// - .AddAuthorizationFilters(): [Authorize] on a tool hides it from tools/list and refuses tools/call for the clients without the policy

// TODO 7: Pass the REST errors to the MCP client: add a call-tool filter to the chain of TODO 6 that catches the exceptions of the
// tools and returns a tool error (new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = message }] }).
// The message must help the model without leaking internals:
// - McpException (but not McpProtocolException): an argument refused by the tool: ex.Message
// - OrdersApiException with StatusCode 400, 404, 409, 422: ex.Detail ?? ex.Title (written by the microservice for its callers)
// - OrdersApiException with 401 or 403: "The orders service refused the request of the MCP server. Contact the team of the orders service."
// - any other OrdersApiException (5xx, timeout, connection refused): "The orders service is unavailable. Try again in a few minutes."
//   and log the details: context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("RestApiMCPServer.Tools").LogWarning(...)
// - any other exception (not McpException, not OperationCanceledException): log it as an error, "The tool failed unexpectedly."
// Hint: .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
//       { try { return await next(context, cancellationToken); } catch (McpException ex) when (ex is not McpProtocolException) { ... } catch (OrdersApiException ex) { ... } ... }))

// TODO 8: Authentication: accept the bearer tokens (JWT) issued for this MCP server by the authorization server, and answer a 401
// with the Protected Resource Metadata (RFC 9728) so that clients know which authorization server and scopes to use
// Hint: builder.Services.AddAuthentication(options => { options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
//           options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme; })
//       .AddJwtBearer(options => { options.MapInboundClaims = false; options.TokenValidationParameters = authorizationServer.ValidationParameters; })
//       .AddMcp(options => options.ResourceMetadata = new ProtectedResourceMetadata { Resource = mcpUrl,
//           AuthorizationServers = [authorizationServer.Issuer], ScopesSupported = [.. DevAuthorizationServer.Scopes], ResourceName = "Orders MCP server" });

// TODO 9: Authorization: one policy per scope (orders.read, orders.write), named after the scope
// Hint: builder.Services.AddAuthorization(options => { foreach (string scope in DevAuthorizationServer.Scopes) {
//           options.AddPolicy(scope, policy => policy.RequireAuthenticatedUser()
//               .RequireAssertion(context => DevAuthorizationServer.HasScope(context.User, scope))); } });

// TODO 10: Health check of the dependency of the MCP server, the REST microservice (Hosting/OrdersApiHealthCheck.cs)
// Hint: builder.Services.AddHealthChecks().AddCheck<OrdersApiHealthCheck>("orders-api");

WebApplication app = builder.Build();

// TODO 11: Use authentication and authorization, then map the endpoints
// - the MCP endpoint, which requires a token with the orders.read scope: app.MapMcp(McpPath).RequireAuthorization(DevAuthorizationServer.ReadScope)
// - /health, anonymous, for the container platform:
//   app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthReportWriter.WriteJsonAsync })
// Hint: app.UseAuthentication(); app.UseAuthorization(); before the Map... calls

// Provided: the token endpoint and the metadata of the development authorization server
authorizationServer.MapEndpoints(app);

// Provided: start the server on its address
app.Urls.Add(string.IsNullOrWhiteSpace(serverSettings.ListenUrls) ? serverSettings.BaseUrl : serverSettings.ListenUrls);
await app.StartAsync();
ColoredConsole.WritePrimaryLogLine($"MCP server listening on {mcpUrl} (Streamable HTTP, stateless, bearer token required)");

#endregion

// --serve (or OrdersMcpServer:Serve = true): no scenario, serve the MCP clients of the other teams until Ctrl+C
if (serveOnly)
{
    ColoredConsole.WriteInfoLine($"MCP server ready for other clients on {mcpUrl}");
    ColoredConsole.WriteSecondaryLogLine($"Token endpoint (development): {authorizationServer.TokenEndpoint}. Press Ctrl+C to stop.");
    await app.WaitForShutdownAsync();
    return;
}

// The scenarios test the server as the consumer teams will use it: an MCP client with a token, and no model.
using HttpClient http = new();
DevClientSettings salesAgent = ConfigurationHelper.GetDevClients()["sales-agent"];
DevClientSettings reportingAgent = ConfigurationHelper.GetDevClients()["reporting-agent"];

// Provided: connects an MCP client to the server with a bearer token, as Lab16_RestApiMCPClient does
#pragma warning disable CS8321 // used by the scenarios you write
async Task<McpClient> ConnectAsync(string clientId, string accessToken) =>
    await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = clientId,
        Endpoint = new Uri(mcpUrl),
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {accessToken}" }
    }));
#pragma warning restore CS8321

#region Scenario 1: Discover and call the tools exposed from the REST API

if (ShouldRunScenario(1))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 1: Discover and call the tools exposed from the REST API ===");

    // TODO 12: Get a token for the sales-agent client (client credentials grant), then connect an MCP client with it
    // Hint: string token = await DevTokenClient.GetAccessTokenAsync(http, authorizationServer.TokenEndpoint, "sales-agent", salesAgent.Secret, mcpUrl);
    //       await using McpClient mcpClient = await ConnectAsync("sales-agent", token);
    //       ColoredConsole.WritePrimaryLogLine($"Connected to {mcpClient.ServerInfo.Title ?? mcpClient.ServerInfo.Name} {mcpClient.ServerInfo.Version} as sales-agent");

    // TODO 13: List the tools (4 tools for 8 REST endpoints) and display them with their schemas and annotations
    // Hint: IList<McpClientTool> tools = await mcpClient.ListToolsAsync(); WriteTools(tools);

    // TODO 14: Call search_products (category "keyboards", maxPrice 100) and get_order (orderId "ORD-1002"), and display the results
    // Hint: CallToolResult products = await mcpClient.CallToolAsync("search_products", new Dictionary<string, object?> { ["category"] = "keyboards", ["maxPrice"] = 100 });
    //       WriteToolResult("search_products(category: keyboards, maxPrice: 100)", products); then the same for get_order
}

#endregion

#region Scenario 2: Pass the REST errors to the MCP client

if (ShouldRunScenario(2))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 2: Pass the REST errors to the MCP client ===");

    // TODO 15: Connect as sales-agent (same as TODO 12)

    // TODO 16: Call the tools on the error cases of the REST API, and display each result with WriteToolResult(label, result):
    // - get_order(orderId: ORD-9999): 404 of the REST API -> tool error with the detail of the microservice
    // - place_order(customerId: CUST-042, lines: [KB-200 x 30]): 409, not enough stock -> tool error with the stock left
    //   Hint: ["lines"] = new[] { new { sku = "KB-200", quantity = 30 } }
    // - get_order(orderId: ORD-0500): 500 (simulated outage) -> generic message
    // - get_product(sku: ../admin): refused by the tool itself, before any call to the REST API
    // Use the labels "get_order(orderId: ORD-9999)", "place_order(CUST-042, KB-200 x 30)", "get_order(orderId: ORD-0500)" and "get_product(sku: ../admin)"

    // TODO 17: Check that the message of the outage does not leak the internal details the microservice logged (orders-db...)
    // Hint: bool leaked = outage.Content.OfType<TextContentBlock>().Any(text => text.Text.Contains("orders-db", StringComparison.OrdinalIgnoreCase));
    //       ColoredConsole.WriteSecondaryLogLine($"  Internal details sent to the client: {(leaked ? "yes" : "no")}");
}

#endregion

#region Scenario 3: Protect the tools with a token, scopes and per-tool authorization

if (ShouldRunScenario(3))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 3: Protect the tools with a token, scopes and per-tool authorization ===");

    // TODO 18: Send a tools/list request WITHOUT a token: the answer is 401, and its WWW-Authenticate header gives the URL of the
    // Protected Resource Metadata
    // Hint: using HttpRequestMessage anonymous = new(HttpMethod.Post, mcpUrl) { Content = new StringContent(
    //           """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", new MediaTypeHeaderValue("application/json")) };
    //       anonymous.Headers.Accept.ParseAdd("application/json, text/event-stream");
    //       using HttpResponseMessage unauthorized = await http.SendAsync(anonymous);
    //       ColoredConsole.WritePrimaryLogLine($"Without a token: {(int)unauthorized.StatusCode} {unauthorized.StatusCode}");
    //       ColoredConsole.WriteSecondaryLogLine($"  WWW-Authenticate: {unauthorized.Headers.WwwAuthenticate}");

    // TODO 19: Read the Protected Resource Metadata at {serverSettings.BaseUrl}/.well-known/oauth-protected-resource{McpPath}
    // and display its "resource", "authorization_servers" and "scopes_supported"
    // Hint: JsonElement metadata = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(metadataUrl));
    //       ColoredConsole.WritePrimaryLogLine($"Protected Resource Metadata ({metadataUrl}):"); then metadata.GetProperty("resource")...

    // TODO 20: Connect as reporting-agent (scope orders.read): list its tools (WriteTools(tools, withSchemas: false), place_order is not
    // there), then call place_order anyway (CUST-042, MS-100 x 1): the server refuses it with a protocol error (an McpException)
    // Hint: ColoredConsole.WritePrimaryLogLine("reporting-agent (scope orders.read):"); ... try { ... } catch (McpException ex)
    //       { ColoredConsole.WriteWarningLine($"place_order as reporting-agent -> refused: {ex.Message}"); }

    // TODO 21: Connect as sales-agent (orders.read orders.write): list its tools (4), and place an order (CUST-042, MS-100 x 2).
    // The order says who placed it: the MCP server, on behalf of sales-agent
    // Hint: ColoredConsole.WritePrimaryLogLine("sales-agent (scopes orders.read orders.write):"); then
    //       WriteToolResult("place_order(CUST-042, MS-100 x 2) as sales-agent", await salesClient.CallToolAsync("place_order", ...));
}

#endregion

#region Scenario 4: Run the MCP server as an independent service

if (ShouldRunScenario(4))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 4: Run the MCP server as an independent service ===");

    // TODO 22: Call the health endpoint the container platform probes, and display its status code and its JSON
    // Hint: using HttpResponseMessage health = await http.GetAsync($"{serverSettings.BaseUrl}/health");
    //       ColoredConsole.WritePrimaryLogLine($"GET /health: {(int)health.StatusCode} {await health.Content.ReadAsStringAsync()}");

    // TODO 23: Stateless: connect two clients (reporting-agent, same token), call get_product(sku: MN-270) with each, and display
    // that both answered and that there is no MCP session id
    // Hint: ColoredConsole.WritePrimaryLogLine($"Two independent connections answered: {(a.IsError != true && b.IsError != true ? "yes" : "no")} (MCP session id: {first.SessionId ?? "none"})");

    // Provided: what a deployment changes, only configuration, through environment variables
    ColoredConsole.WritePrimaryLogLine("Deployment settings (environment variables):");
    ColoredConsole.WriteSecondaryLogLine($"  OrdersMcpServer__BaseUrl={serverSettings.BaseUrl}");
    ColoredConsole.WriteSecondaryLogLine($"  OrdersApi__BaseUrl={ordersApiSettings.BaseUrl}");
    ColoredConsole.WriteSecondaryLogLine("  OrdersApi__ApiKey=*** (a secret: vault or secret of the container platform)");
    ColoredConsole.WriteSecondaryLogLine("  Containers: docker compose -f LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/deploy/docker-compose.yml up --build");
}

#endregion

await app.StopAsync();
