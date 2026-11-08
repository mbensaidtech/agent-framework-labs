# Lab 16 - Use a Remote MCP Server of Another Team (consumer side)

## Objective

You are the **consumer team**: you own an AI agent built with **Microsoft Agent Framework** and **Azure OpenAI**. Another team, which owns an orders microservice, has published some of its capabilities as a **remote MCP server** ([Lab16_RestApiMCPServer](../Lab16_RestApiMCPServer/README.md)). It gave you two things only: the **URL** of the MCP server and **client credentials**.

You connect your agent to that server, with a token, and let it search the catalog, read orders and place orders, without knowing anything about the REST API behind it. Then you see what the provider team controls: the errors your agent receives, and the tools a read-only client does not get.

| Scenario | What you do |
|----------|-------------|
| **Scenario 1** — Give the remote tools to an agent | Get a token (client credentials), connect an MCP client with it, give the tools to a `SalesAssistant` and ask a question that needs two tools |
| **Scenario 2** — Place an order and handle the errors | The REST API answers 409 (not enough stock): the agent reads the tool error and orders what is left; then an unknown order (404) |
| **Scenario 3** — A read-only consumer | The `reporting-agent` client (scope `orders.read`) does not get `place_order`; its agent says it cannot order, and a direct call is refused by the server |

## What You Will Learn

- How to authenticate an MCP client to a protected remote MCP server: the OAuth 2.0 **client credentials** grant, the `resource` parameter, the `Authorization: Bearer` header (`HttpClientTransportOptions.AdditionalHeaders`)
- How to give the tools of a remote MCP server to an agent (`ListToolsAsync`, `McpClientTool` as `AITool`)
- How tool errors (`isError: true`) reach the model and let the agent adapt
- Why authorization belongs to the server: what a client may do does not depend on the instructions of its agent

## Prerequisites

- .NET 10 SDK or later
- An Azure OpenAI chat deployment, configured as for the other labs (root README)
- **The MCP server of Lab16_RestApiMCPServer running** on `http://localhost:5310` (Step 0)

## Project Structure

```
Lab16_RestApiMCPClient/
├── README.md
├── Start/                        <-- Your working folder
│   ├── Program.cs                    TODO 1-14
│   ├── LabPrompts.cs                 PROVIDED: names, instructions and questions of the agents
│   ├── DevTokenClient.cs             PROVIDED: client credentials grant (POST /oauth/token)
│   ├── AgentConsole.cs               PROVIDED: display of tools, tool calls, answers, token usage
│   ├── RemoteMcpServerSettings.cs, AzureOpenAISettings.cs, ConfigurationHelper.cs
│   └── appsettings.json              URL of the MCP server, token endpoint, client credentials (development values)
└── Solution/                     <-- Complete reference implementation
```

This project references **no** project of the provider team: only `ModelContextProtocol` (the MCP client) and Agent Framework.

## Getting Started

### Step 0: Start the MCP server of the provider team

In another terminal, keep it running:

```bash
cd LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/Solution
dotnet run -- --serve
```

It starts the orders microservice if needed, then prints `MCP server ready for other clients on http://localhost:5310/mcp`. (Or run the provider side in containers: `docker compose -f LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/deploy/docker-compose.yml up --build`.)

Then run this lab:

```bash
cd LearningLabs/AzureOpenAI/Lab16_RestApiMCPClient/Solution
dotnet run
```

The data of the microservice is in memory: scenario 2 orders the last units of `KB-200`. Restart the server to get the initial stock back before running scenario 2 again.

### Step 1: Setup

| TODO | Task | Hint |
|------|------|------|
| 1-3 | Azure OpenAI client and `ChatClient` | Same as the other labs |
| 4 | `ConnectAsync(ClientCredentials credentials)`: token, then MCP client | `DevTokenClient.GetAccessTokenAsync(http, remoteServer.TokenEndpoint, credentials.ClientId, credentials.ClientSecret, resource: remoteServer.Url)`, then `McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint, TransportMode = HttpTransportMode.StreamableHttp, AdditionalHeaders = { ["Authorization"] = $"Bearer {token}" } }))` |

### Step 2: Scenarios

| TODO | Scenario | Task |
|------|----------|------|
| 5-7 | 1 | Connect as `SalesAgent`, `ListToolsAsync()`, `chatClient.AsAIAgent(..., tools: [.. tools.Cast<AITool>()])`, `RunAsync(Questions.KeyboardsAndOrder)` |
| 8-10 | 2 | Same agent; `Questions.OrderTenKeyboards` (409 then a second `place_order`), `Questions.UnknownOrder` (404) |
| 11-14 | 3 | Connect as `ReportingAgent` (3 tools), `ReportingAssistant` with `Questions.StockAndOrder`, then a direct `CallToolAsync("place_order", ...)` in a `try`/`catch (McpException)` |

## How it works

```
SalesAssistant (this program)                     Orders MCP server (provider team)            OrdersApi (provider team)
  1. POST /oauth/token  client_credentials ──────► token (aud = http://localhost:5310/mcp, scope)
  2. tools/list   Authorization: Bearer ...  ───► 4 tools (3 for reporting-agent)
  3. model: "call place_order(KB-200 x 10)"
     tools/call place_order ───────────────────► POST /api/orders  X-Api-Key: <MCP server's key> ──► 409 Only 8 left
     ◄─────────────── result { isError: true, "Only 8 unit(s) of KB-200 ... left" } ◄──────────────────┘
  4. model: "call place_order(KB-200 x 8)" ─────► POST /api/orders ──────────────────────────────────► 201 ORD-1004
```

1. **Your credentials, not the provider's.** You only hold a client id and a secret for the **authorization server** (here the development server hosted by the MCP server; in production Microsoft Entra ID). You ask for a token for the **resource** `http://localhost:5310/mcp`; the token is only valid there. You never see the API key of the microservice.
2. **The tools are ordinary `AIFunction`s.** `ListToolsAsync()` returns `McpClientTool`s: name, description and JSON schema come from the server. When the model calls one, the agent sends `tools/call` to the server, with the token, and gives the result to the model.
3. **Tool errors are for the model.** A `404` or a `409` of the REST API comes back as a result with `isError: true` and a message written by the provider team: the model reads it, explains it, or corrects its call (scenario 2).
4. **Authorization is enforced by the server.** The `reporting-agent` token has the scope `orders.read` only: the server does not list `place_order` and refuses a direct call. Instructions and models can be wrong or manipulated (prompt injection); the scopes of the client cannot.
5. **Whose tokens?** The agent's model calls are made here, with your Azure OpenAI deployment. The MCP server has no model: its calls cost no tokens.

In production, prefer the libraries of your identity provider (for Microsoft Entra ID: `Azure.Identity` with a `ClientSecretCredential`, a certificate, or better a **managed identity**) to `DevTokenClient`, and cache the token until it expires. The MCP C# SDK also has an OAuth client (`HttpClientTransportOptions.OAuth`) for the **authorization code** flow of interactive hosts, which discovers the authorization server from the Protected Resource Metadata.

## Key Concepts

| Concept | Description |
|---------|-------------|
| Client credentials grant | OAuth 2.0 flow of a machine client: client id + secret (or certificate, managed identity) → access token |
| `resource` (RFC 8707) | The resource the token is requested for; becomes its audience |
| `HttpClientTransportOptions.AdditionalHeaders` | Headers sent with every MCP request: here `Authorization: Bearer <token>` |
| `McpClientTool` | A tool of an MCP server seen as an `AIFunction`: give it to an agent with `tools: [.. tools.Cast<AITool>()]` |
| Tool error | `isError: true` in the result of `tools/call`: given to the model like any result |
| `McpException` | A protocol error (forbidden tool, unknown tool): thrown by `CallToolAsync`, never shown to the model |

## Expected Output

```
Endpoint: https://your-resource.openai.azure.com/
Deployment: your-deployment-name
Remote MCP server: http://localhost:5310/mcp
----------------------------------------
=== Scenario 1: Give the tools of the remote MCP server to an agent ===
MCP tools for sales-agent (4): get_order, get_product, place_order, search_products
User: Which keyboards cost less than 100 euros and are in stock? And what is the status of the order ORD-1002?
Tool called: search_products(category: "keyboards", maxPrice: 100)
  Result: {"count":2,"products":[{"sku":"KB-100",...,"stock":25},{"sku":"KB-300",...,"stock":0}]}
Tool called: get_order(orderId: "ORD-1002")
  Result: {"orderId":"ORD-1002","customerId":"CUST-042","status":"Pending",...}
Agent answer:
The Compact mechanical keyboard (KB-100, 89.90 euros, 25 in stock) ... The order ORD-1002 is Pending ...
----------------------------------------
Token Usage: ...
----------------------------------------
=== Scenario 2: Let the agent place an order and handle the errors of the REST API ===
User: Place an order of 10 units of KB-200 for the customer CUST-042 directly, ...
Tool called: place_order(customerId: "CUST-042", lines: [{"sku":"KB-200","quantity":10}])
  Result: Only 8 unit(s) of KB-200 (Wireless ergonomic keyboard) left in stock, 10 requested. (tool error)
Tool called: place_order(customerId: "CUST-042", lines: [{"sku":"KB-200","quantity":8}])
  Result: {"orderId":"ORD-1004",...,"total":1032.00,...,"placedBy":"orders-mcp-server on behalf of sales-agent"}
Agent answer:
The order ORD-1004 has been placed: 8 units, total 1032.00 euros.
User: What is the status of the order ORD-9999?
Tool called: get_order(orderId: "ORD-9999")
  Result: Order ORD-9999 does not exist. (tool error)
Agent answer:
The order ORD-9999 does not exist ...
----------------------------------------
=== Scenario 3: A read-only consumer cannot order, whatever its agent decides ===
MCP tools for reporting-agent (3): get_order, get_product, search_products
User: How many 27-inch monitors (MN-270) are in stock? Also, place an order of one MN-270 for the customer CUST-077.
Tool called: get_product(sku: "MN-270")
  Result: {"sku":"MN-270","name":"27-inch QHD monitor","category":"monitors","price":299.00,"stock":12}
Agent answer:
There are 12 units ... I cannot place an order: my access does not allow it.
Direct call of place_order as reporting-agent -> refused by the server: Request failed (remote): Access forbidden: This tool requires authorization.
```

Answers vary from one run to another: the model may check the stock before ordering in scenario 2 (then it orders 8 units at once), or call a tool more than once.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `Connection refused` on `http://localhost:5310/oauth/token` | The MCP server is not running: Step 0 (`dotnet run -- --serve` in `Lab16_RestApiMCPServer/Solution`) |
| `Token request refused (401): invalid_client` | Wrong `ClientSecret` in `RemoteMcpServer:Clients` |
| `Token request refused (400): invalid_target` | `RemoteMcpServer:Url` differs from the URL of the server (`OrdersMcpServer:BaseUrl` + `/mcp`) |
| `401` when connecting | The server restarted since the token was issued (its development signing key is random per run): run the lab again |
| Scenario 2: `Only 0 unit(s) of KB-200 left` | The stock was used by a previous run: restart the MCP server (and the microservice it started) |
| `'AzureOpenAI:Endpoint' is not configured` | Configure Azure OpenAI as for the other labs |

## Provided Files

The following files are provided and should not be modified: `LabPrompts.cs`, `DevTokenClient.cs`, `AgentConsole.cs`, `RemoteMcpServerSettings.cs`, `AzureOpenAISettings.cs`, `ConfigurationHelper.cs`.
