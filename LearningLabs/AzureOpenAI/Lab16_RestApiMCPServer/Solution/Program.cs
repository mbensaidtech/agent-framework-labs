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

// Solution of TODO 5: Connect the MCP server to the REST API: a typed HttpClient for OrdersApi (RestClient/OrdersApiClient.cs), with the
// address of the microservice, the API key OF THE MCP SERVER (its own identity, never the token of an MCP client: no token
// passthrough) and a timeout (a slow API must not block the agents)
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<OrdersApiClient>(http =>
{
    http.BaseAddress = new Uri(ordersApiSettings.BaseUrl + "/");
    http.DefaultRequestHeaders.Add("X-Api-Key", ordersApiSettings.ApiKey);
    http.Timeout = TimeSpan.FromSeconds(ordersApiSettings.TimeoutSeconds);
});

// Solution of TODO 6: Register the MCP server
// - Streamable HTTP transport, stateless: every request stands alone, so several instances can run behind a load balancer
// - the tools: only the [McpServerTool] methods of CatalogTools and OrderTools (Tools/), i.e. 4 of the 8 REST endpoints
// - authorization filters: [Authorize] on a tool hides it from tools/list and refuses tools/call for the clients without the policy
// - a call-tool filter that turns the errors of the REST API into MCP tool errors (step 4)
builder.Services
    .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "orders-mcp-server", Title = "Orders MCP server", Version = "1.0.0" })
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<CatalogTools>()
    .WithTools<OrderTools>()
    .AddAuthorizationFilters()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        // TODO 7: Pass the REST errors to the MCP client. A tool error (isError: true) goes back to the model, which can explain
        // it or correct its call; a protocol error would stop the call. The message must help, without leaking internals:
        // - 400, 404, 409: the detail written by the microservice for its callers (unknown order, not enough stock...)
        // - 401, 403: a configuration problem of the MCP server (its API key), not something the client can fix
        // - 5xx, timeout, connection refused: a generic message; the details go to the log of the MCP server only
        // - an argument refused by the tool (McpException): its own message
        try
        {
            return await next(context, cancellationToken);
        }
        catch (McpException ex) when (ex is not McpProtocolException)
        {
            // An argument refused by the tool itself (Tools/): its message is written for the client
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
        }
        catch (OrdersApiException ex)
        {
            ILogger logger = context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("RestApiMCPServer.Tools");
            logger.LogWarning("Tool {Tool} failed: {Error}", context.Params?.Name, ex.Message);

            string message = ex.StatusCode switch
            {
                400 or 404 or 409 or 422 => ex.Detail ?? ex.Title,
                401 or 403 => "The orders service refused the request of the MCP server. Contact the team of the orders service.",
                _ => "The orders service is unavailable. Try again in a few minutes."
            };
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = message }] };
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            // A bug of the MCP server itself: log it, and tell the client nothing about it
            context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("RestApiMCPServer.Tools")
                .LogError(ex, "Tool {Tool} failed unexpectedly", context.Params?.Name);
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "The tool failed unexpectedly." }] };
        }
    }));

// Solution of TODO 8: Authentication. The MCP endpoint accepts bearer tokens (JWT) issued for it by the authorization server.
// AddMcp(): on a 401, the WWW-Authenticate header points to the Protected Resource Metadata (RFC 9728), which tells the
// clients which authorization server and which scopes to use (MCP authorization specification)
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep the claim names of the token: client_id, scope
        options.TokenValidationParameters = authorizationServer.ValidationParameters;
    })
    .AddMcp(options => options.ResourceMetadata = new ProtectedResourceMetadata
    {
        Resource = mcpUrl,
        AuthorizationServers = [authorizationServer.Issuer],
        ScopesSupported = [.. DevAuthorizationServer.Scopes],
        ResourceName = "Orders MCP server"
    });

// Solution of TODO 9: Authorization: one policy per scope. orders.read is needed to use the MCP endpoint at all,
// orders.write to see and call place_order ([Authorize(Policy = "orders.write")] in OrderTools)
builder.Services.AddAuthorization(options =>
{
    foreach (string scope in DevAuthorizationServer.Scopes)
    {
        options.AddPolicy(scope, policy => policy.RequireAuthenticatedUser()
            .RequireAssertion(context => DevAuthorizationServer.HasScope(context.User, scope)));
    }
});

// Solution of TODO 10: Health check: the MCP server and its dependency, the REST microservice (Hosting/OrdersApiHealthCheck.cs)
builder.Services.AddHealthChecks().AddCheck<OrdersApiHealthCheck>("orders-api");

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// Solution of TODO 11: Map the endpoints
// - the MCP endpoint, which requires a token with the orders.read scope
// - /health, anonymous, for the container platform (JSON with the status of each check)
// - the token endpoint and the metadata of the development authorization server (provided)
app.MapMcp(McpPath).RequireAuthorization(DevAuthorizationServer.ReadScope);
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthReportWriter.WriteJsonAsync });
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
async Task<McpClient> ConnectAsync(string clientId, string accessToken) =>
    await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = clientId,
        Endpoint = new Uri(mcpUrl),
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {accessToken}" }
    }));

#region Scenario 1: Discover and call the tools exposed from the REST API

if (ShouldRunScenario(1))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 1: Discover and call the tools exposed from the REST API ===");

    // Step 1: Get a token for the sales-agent client (client credentials), then connect an MCP client with it
    string token = await DevTokenClient.GetAccessTokenAsync(http, authorizationServer.TokenEndpoint, "sales-agent", salesAgent.Secret, mcpUrl);
    await using McpClient mcpClient = await ConnectAsync("sales-agent", token);
    ColoredConsole.WritePrimaryLogLine($"Connected to {mcpClient.ServerInfo.Title ?? mcpClient.ServerInfo.Name} {mcpClient.ServerInfo.Version} as sales-agent");

    // Step 2: List the tools: 4 tools for 8 REST endpoints, each with its description, input schema, output schema and annotations
    IList<McpClientTool> tools = await mcpClient.ListToolsAsync();
    WriteTools(tools);

    // Step 3: Call two tools. The MCP server calls the REST API and returns its data as structured content
    CallToolResult products = await mcpClient.CallToolAsync("search_products",
        new Dictionary<string, object?> { ["category"] = "keyboards", ["maxPrice"] = 100 });
    WriteToolResult("search_products(category: keyboards, maxPrice: 100)", products);

    CallToolResult order = await mcpClient.CallToolAsync("get_order", new Dictionary<string, object?> { ["orderId"] = "ORD-1002" });
    WriteToolResult("get_order(orderId: ORD-1002)", order);
}

#endregion

#region Scenario 2: Pass the REST errors to the MCP client

if (ShouldRunScenario(2))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 2: Pass the REST errors to the MCP client ===");

    string token = await DevTokenClient.GetAccessTokenAsync(http, authorizationServer.TokenEndpoint, "sales-agent", salesAgent.Secret, mcpUrl);
    await using McpClient mcpClient = await ConnectAsync("sales-agent", token);

    // Step 1: 404 of the REST API -> tool error with the detail of the microservice
    WriteToolResult("get_order(orderId: ORD-9999)",
        await mcpClient.CallToolAsync("get_order", new Dictionary<string, object?> { ["orderId"] = "ORD-9999" }));

    // Step 2: 409 (not enough stock) -> tool error with the stock left: the model of an agent can propose a smaller quantity
    WriteToolResult("place_order(CUST-042, KB-200 x 30)",
        await mcpClient.CallToolAsync("place_order", new Dictionary<string, object?>
        {
            ["customerId"] = "CUST-042",
            ["lines"] = new[] { new { sku = "KB-200", quantity = 30 } }
        }));

    // Step 3: 500 of the REST API (simulated outage on ORD-0500) -> a generic message; the details stay in the server log
    CallToolResult outage = await mcpClient.CallToolAsync("get_order", new Dictionary<string, object?> { ["orderId"] = "ORD-0500" });
    WriteToolResult("get_order(orderId: ORD-0500)", outage);
    bool leaked = outage.Content.OfType<TextContentBlock>().Any(text => text.Text.Contains("orders-db", StringComparison.OrdinalIgnoreCase));
    ColoredConsole.WriteSecondaryLogLine($"  Internal details sent to the client: {(leaked ? "yes" : "no")}");

    // Step 4: invalid argument -> refused by the tool itself, before any call to the REST API
    WriteToolResult("get_product(sku: ../admin)",
        await mcpClient.CallToolAsync("get_product", new Dictionary<string, object?> { ["sku"] = "../admin" }));
}

#endregion

#region Scenario 3: Protect the tools with a token, scopes and per-tool authorization

if (ShouldRunScenario(3))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 3: Protect the tools with a token, scopes and per-tool authorization ===");

    // Step 1: Without a token: 401, and the WWW-Authenticate header tells where the Protected Resource Metadata is
    using HttpRequestMessage anonymous = new(HttpMethod.Post, mcpUrl)
    {
        Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", new MediaTypeHeaderValue("application/json"))
    };
    anonymous.Headers.Accept.ParseAdd("application/json, text/event-stream");
    using HttpResponseMessage unauthorized = await http.SendAsync(anonymous);
    ColoredConsole.WritePrimaryLogLine($"Without a token: {(int)unauthorized.StatusCode} {unauthorized.StatusCode}");
    ColoredConsole.WriteSecondaryLogLine($"  WWW-Authenticate: {unauthorized.Headers.WwwAuthenticate}");

    // Step 2: Read the Protected Resource Metadata: the authorization server and the scopes a client must use
    string metadataUrl = $"{serverSettings.BaseUrl}/.well-known/oauth-protected-resource{McpPath}";
    JsonElement metadata = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(metadataUrl));
    ColoredConsole.WritePrimaryLogLine($"Protected Resource Metadata ({metadataUrl}):");
    ColoredConsole.WriteSecondaryLogLine($"  resource: {metadata.GetProperty("resource")}, authorization_servers: {metadata.GetProperty("authorization_servers")}, scopes_supported: {metadata.GetProperty("scopes_supported")}");

    // Step 3: The reporting-agent client only has orders.read: place_order is not in its tools, and a direct call is refused
    string readToken = await DevTokenClient.GetAccessTokenAsync(http, authorizationServer.TokenEndpoint, "reporting-agent", reportingAgent.Secret, mcpUrl);
    await using (McpClient readOnlyClient = await ConnectAsync("reporting-agent", readToken))
    {
        ColoredConsole.WritePrimaryLogLine("reporting-agent (scope orders.read):");
        WriteTools(await readOnlyClient.ListToolsAsync(), withSchemas: false);
        try
        {
            WriteToolResult("place_order as reporting-agent", await readOnlyClient.CallToolAsync("place_order", new Dictionary<string, object?>
            {
                ["customerId"] = "CUST-042",
                ["lines"] = new[] { new { sku = "MS-100", quantity = 1 } }
            }));
        }
        catch (McpException ex)
        {
            ColoredConsole.WriteWarningLine($"place_order as reporting-agent -> refused: {ex.Message}");
        }
    }

    // Step 4: The sales-agent client has orders.read and orders.write: it places an order. The microservice records that the
    // MCP server placed it on behalf of sales-agent (the MCP server called with its own API key, never with the client's token)
    string writeToken = await DevTokenClient.GetAccessTokenAsync(http, authorizationServer.TokenEndpoint, "sales-agent", salesAgent.Secret, mcpUrl);
    await using (McpClient salesClient = await ConnectAsync("sales-agent", writeToken))
    {
        ColoredConsole.WritePrimaryLogLine("sales-agent (scopes orders.read orders.write):");
        WriteTools(await salesClient.ListToolsAsync(), withSchemas: false);
        WriteToolResult("place_order(CUST-042, MS-100 x 2) as sales-agent", await salesClient.CallToolAsync("place_order", new Dictionary<string, object?>
        {
            ["customerId"] = "CUST-042",
            ["lines"] = new[] { new { sku = "MS-100", quantity = 2 } }
        }));
    }
}

#endregion

#region Scenario 4: Run the MCP server as an independent service

if (ShouldRunScenario(4))
{
    ColoredConsole.WriteDividerLine();
    ColoredConsole.WriteInfoLine("=== Scenario 4: Run the MCP server as an independent service ===");

    // Step 1: The health endpoint the container platform probes: the MCP server and its dependency, the REST microservice
    using HttpResponseMessage health = await http.GetAsync($"{serverSettings.BaseUrl}/health");
    ColoredConsole.WritePrimaryLogLine($"GET /health: {(int)health.StatusCode} {await health.Content.ReadAsStringAsync()}");

    // Step 2: Stateless: two clients, two connections, no MCP session to share. Any instance behind a load balancer can answer
    string token = await DevTokenClient.GetAccessTokenAsync(http, authorizationServer.TokenEndpoint, "reporting-agent", reportingAgent.Secret, mcpUrl);
    await using McpClient first = await ConnectAsync("reporting-agent", token);
    await using McpClient second = await ConnectAsync("reporting-agent", token);
    CallToolResult a = await first.CallToolAsync("get_product", new Dictionary<string, object?> { ["sku"] = "MN-270" });
    CallToolResult b = await second.CallToolAsync("get_product", new Dictionary<string, object?> { ["sku"] = "MN-270" });
    ColoredConsole.WritePrimaryLogLine($"Two independent connections answered: {(a.IsError != true && b.IsError != true ? "yes" : "no")} (MCP session id: {first.SessionId ?? "none"})");

    // Step 3: What a deployment changes: only configuration, through environment variables
    ColoredConsole.WritePrimaryLogLine("Deployment settings (environment variables):");
    ColoredConsole.WriteSecondaryLogLine($"  OrdersMcpServer__BaseUrl={serverSettings.BaseUrl}");
    ColoredConsole.WriteSecondaryLogLine($"  OrdersApi__BaseUrl={ordersApiSettings.BaseUrl}");
    ColoredConsole.WriteSecondaryLogLine("  OrdersApi__ApiKey=*** (a secret: vault or secret of the container platform)");
    ColoredConsole.WriteSecondaryLogLine("  Containers: docker compose -f LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/deploy/docker-compose.yml up --build");
}

#endregion

await app.StopAsync();
