# Lab 16 - Expose a REST API as a Remote MCP Server (provider side)

## Objective

Your team owns a **REST microservice**. It needs no AI, no conversation, no agent. But other teams build AI agents, and these agents would be more useful if they could use **some** of the capabilities of your microservice.

In this lab you are the **provider team**: you put a **remote MCP server** in front of your existing REST API, and you expose a **chosen subset** of its endpoints as **MCP tools** that any MCP client (an agent of another team, VS Code, MCP Inspector...) can discover and call. You do it **without writing an agent and without any model**: this lab needs no Azure OpenAI configuration.

The consumer side, an Azure OpenAI agent of another team that uses your tools, is the companion lab **[Lab16_RestApiMCPClient](../Lab16_RestApiMCPClient/README.md)**.

```
 Provider team (this lab)                                                          Consumer team (Lab16_RestApiMCPClient)
┌──────────────────────────────┐   HTTP + API key   ┌────────────────────────────────────────┐   MCP (Streamable HTTP)  ┌───────────────────────┐
│ OrdersApi (existing, as is)  │◄───────────────────┤ Orders MCP server (Start / Solution)   │◄─────────────────────────┤ AI agent (Azure OpenAI)│
│ REST: 8 endpoints            │  X-Api-Key of the  │ 4 tools = 4 chosen endpoints           │  Bearer token (OAuth,    │ + MCP client          │
│ /api/products, /api/orders,  │  MCP server        │ errors -> MCP tool errors              │  client credentials,     │                       │
│ /api/customers, /health      │  X-On-Behalf-Of    │ token + scopes + per-tool authorization│  scopes orders.read/write│                       │
│ no MCP, no AI                │                    │ /health, stateless, container          │                          │                       │
└──────────────────────────────┘                    └────────────────────────────────────────┘                          └───────────────────────┘
   port 5300                                            port 5310, endpoint /mcp
```

Three programs, three processes, three release cycles: the microservice is **never modified**, the MCP server is a **separate deployable** owned by the provider team, and the agent belongs to another team.

| Scenario | What you do |
|----------|-------------|
| **Scenario 1** — Discover and call the tools | Map 4 of the 8 REST endpoints to MCP tools (names, descriptions, input **and output** schemas, annotations), then list and call them as an MCP client |
| **Scenario 2** — Pass the REST errors to the MCP client | Turn 404, 409, 500 and invalid arguments into **tool errors** with a useful message, without leaking internals |
| **Scenario 3** — Protect the tools | Bearer tokens issued for the MCP server (audience), scopes, Protected Resource Metadata, a write tool only for the clients with `orders.write` |
| **Scenario 4** — Run as an independent service | Health endpoint that checks the microservice, stateless transport, configuration by environment variables, Docker image and docker compose |

## Does an existing lab already cover this?

No. The other MCP labs cover different needs:

| Lab | Side | What is exposed or consumed | Agent in the MCP server? |
|-----|------|-----------------------------|--------------------------|
| [Lab04](../Lab04-AIAgentWithMCPClient/README.md) | **Client** | An agent consumes an existing third-party MCP server (Hugging Face) | — |
| [Lab10](../Lab10_ExposeAIAgentAsMCPTool/README.md) | Server | **An AI agent** becomes one MCP tool with a `query` parameter (`AsAIFunction`); no authentication | **Yes**: every call runs a model, with the provider's tokens |
| **Lab16 (this lab)** | Server **and** client | **REST endpoints of an existing microservice** become typed MCP tools; authentication, authorization, errors, deployment | **No**: deterministic calls to the REST API, no model, no token cost |

Choose Lab10's approach when the capability you share needs reasoning (a question in natural language, several steps). Choose this lab's approach when your microservice already does the job and you only need to make it usable by agents.

## What You Will Learn

- How to connect an MCP server to an existing REST API with a typed `HttpClient` (base address, service credential, timeout)
- How to **select** the endpoints to expose and turn each into an MCP tool with `[McpServerToolType]`, `[McpServerTool]` and `[Description]`: name, title, description, input schema, **output schema** (`UseStructuredContent`) and **annotations** (`ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld`)
- How to validate tool arguments before they reach the REST API (`McpException`)
- How to turn REST errors into MCP **tool errors** (`isError: true`) with a call-tool filter (`WithRequestFilters`, `AddCallToolFilter`)
- How to protect a remote MCP server as an **OAuth 2.0 protected resource**: JWT bearer tokens, audience, scopes, `AddMcp()` and the **Protected Resource Metadata** (RFC 9728), `[Authorize]` on a tool and `AddAuthorizationFilters()`
- Why the MCP server calls the microservice with **its own** credential, never with the client's token (no token passthrough), and how to keep an audit of on whose behalf it calls
- How to make the server deployable on its own: stateless Streamable HTTP, `/health`, environment variables, Docker

## Prerequisites

- .NET 10 SDK or later
- **No Azure OpenAI configuration**: the MCP server has no model
- Ports **5300** (OrdersApi) and **5310** (MCP server) free
- Optional: Docker, for the deployment part; Node.js, for MCP Inspector

## Project Structure

```
Lab16_RestApiMCPServer/
├── README.md
├── OrdersApi/                    <-- PROVIDED: the existing REST microservice (do not edit). No MCP, no AI
│   ├── Program.cs                    8 endpoints, API key authentication, in-memory data
│   └── appsettings.json              port 5300, API keys of its callers
├── Start/                        <-- Your working folder: the MCP server
│   ├── Program.cs                    TODO 5-23: the server (DI, MCP, errors, auth, health) and the scenarios
│   ├── Tools/
│   │   ├── CatalogTools.cs           TODO 1-2: search_products, get_product
│   │   └── OrderTools.cs             TODO 3-4: get_order, place_order
│   ├── RestClient/                   PROVIDED: typed HTTP client of OrdersApi and the data types (with descriptions)
│   ├── Auth/                         PROVIDED: development authorization server (stands in for Microsoft Entra ID), token client
│   ├── Hosting/                      PROVIDED: starts OrdersApi if needed, health check, health report writer
│   ├── McpConsole.cs                 PROVIDED: console display of tools and results
│   ├── Settings.cs, ConfigurationHelper.cs, appsettings.json
│   └── RestApiMCPServer.csproj       ModelContextProtocol.AspNetCore 2.2.0, JwtBearer: no Agent Framework, no OpenAI
├── Solution/                     <-- Complete reference implementation
└── deploy/                       <-- Dockerfiles of both services and docker-compose.yml
```

## Getting Started

### Step 0: Run the solution

```bash
cd LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/Solution
dotnet run
```

The program first makes sure the microservice runs: if nothing answers on `http://localhost:5300/health`, it starts `OrdersApi` **as a separate process** (`dotnet run`, the first time takes a few seconds to build) and stops it at the end. You can also start it yourself in another terminal (`cd ../OrdersApi && dotnet run`): the lab then uses it.

Then it starts the MCP server on `http://localhost:5310/mcp` and runs the four scenarios as an MCP client, the way a consumer team would.

To keep the server running for the consumer lab, MCP Inspector or VS Code:

```bash
dotnet run -- --serve
```

### Step 1: Map the REST endpoints to MCP tools (`Tools/`)

The microservice has 8 endpoints. The provider team decides that agents may search the catalog, read a product, read an order and place an order. Cancelling and deleting orders stay in the back office, and the customer endpoint (personal data) is not exposed.

| REST endpoint | MCP tool | Why |
|---------------|----------|-----|
| `GET /api/products?category=&maxPrice=` | `search_products` | read-only, useful to any assistant |
| `GET /api/products/{sku}` | `get_product` | read-only |
| `GET /api/orders/{orderId}` | `get_order` | read-only |
| `POST /api/orders` | `place_order` | writes: only for the clients with the `orders.write` scope |
| `POST /api/orders/{id}/cancel` | — | back-office process, not for agents |
| `DELETE /api/orders/{id}` | — | destructive administration |
| `GET /api/customers/{id}` | — | personal data (GDPR): not needed by the agents |
| `GET /health` | — | operations |

| TODO | File | Task | Hint |
|------|------|------|------|
| 1 | `Tools/CatalogTools.cs` | `search_products` | `[McpServerTool(Name = "search_products", Title = ..., ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]`, `[Description]` on the method and each parameter, return `ProductList` |
| 2 | `Tools/CatalogTools.cs` | `get_product`, with validation | `if (!SkuFormat().IsMatch(sku)) throw new McpException(...)` |
| 3 | `Tools/OrderTools.cs` | `get_order`, with validation | same pattern, `OrderIdFormat()` |
| 4 | `Tools/OrderTools.cs` | `place_order`, a write tool | `ReadOnly = false, Destructive = false, Idempotent = false`, `[Authorize(Policy = DevAuthorizationServer.WriteScope)]` |

What the model of an agent sees of a tool is **only** its name, its description, its input schema (generated from the parameters and their `[Description]`) and, with `UseStructuredContent`, its output schema (generated from the return type: see the descriptions on the records of `RestClient/OrdersApiModels.cs`). Write them for a model: what the tool does, when to use it, the format of each value, what an error means.

### Step 2: Build the MCP server (`Program.cs`, region *The MCP server*)

| TODO | Task | Hint |
|------|------|------|
| 5 | Typed HTTP client of the microservice | `builder.Services.AddHttpContextAccessor()`, `AddHttpClient<OrdersApiClient>(http => { BaseAddress, X-Api-Key, Timeout })` |
| 6 | Register the MCP server | `AddMcpServer(options => options.ServerInfo = ...)`, `.WithHttpTransport(options => options.Stateless = true)`, `.WithTools<CatalogTools>().WithTools<OrderTools>()`, `.AddAuthorizationFilters()` |
| 7 | REST errors → tool errors | `.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) => { try { return await next(context, ct); } catch (...) { return new CallToolResult { IsError = true, ... }; } }))` |
| 8 | Authentication | `AddAuthentication(...)` with `JwtBearerDefaults` to authenticate and `McpAuthenticationDefaults` to challenge, `.AddJwtBearer(...)`, `.AddMcp(options => options.ResourceMetadata = new ProtectedResourceMetadata { ... })` |
| 9 | Authorization: one policy per scope | `AddAuthorization(options => options.AddPolicy(scope, policy => policy.RequireAssertion(...)))` |
| 10 | Health check of the microservice | `AddHealthChecks().AddCheck<OrdersApiHealthCheck>("orders-api")` |
| 11 | Middleware and endpoints | `UseAuthentication()`, `UseAuthorization()`, `MapMcp(McpPath).RequireAuthorization(DevAuthorizationServer.ReadScope)`, `MapHealthChecks("/health", ...)` |

### Step 3: Test the server as a consumer would (scenarios)

| TODO | Scenario | Task |
|------|----------|------|
| 12-14 | 1 | Token for `sales-agent`, connect, `ListToolsAsync()` + `WriteTools`, `CallToolAsync("search_products", ...)` and `get_order` |
| 15-17 | 2 | `get_order(ORD-9999)` (404), `place_order(KB-200 x 30)` (409), `get_order(ORD-0500)` (500), `get_product("../admin")` (invalid), and no internal details in the outage message |
| 18-21 | 3 | Request without token (401 + `WWW-Authenticate`), Protected Resource Metadata, `reporting-agent` (3 tools, `place_order` refused), `sales-agent` (4 tools, order placed on its behalf) |
| 22-23 | 4 | `GET /health`, two independent connections (stateless) |

## How it works

### 1. One process per role, the MCP server in the middle

The MCP server is an **adapter** (a facade) between two protocols: MCP on one side, the REST API on the other. The microservice does not change; the consumer never sees its URL nor its API key. The provider team can change the REST API (new fields, a v2 endpoint) and keep the MCP contract stable, or change the MCP contract (better descriptions) without touching the microservice.

### 2. From an endpoint to a tool

```
GET /api/orders/{orderId}         [McpServerTool(Name = "get_order", Title = "Get an order", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
  200 -> Order JSON          =>   [Description("Gets an order by its id: ...")]
  404 -> problem details          public async Task<Order> GetOrder([Description("Id of the order, e.g. ORD-1002.")] string orderId, CancellationToken ct)
                                      => validate orderId, then await ordersApi.GetOrderAsync(orderId, ct)
```

- `tools/list` returns, for each tool: `name`, `title`, `description`, `inputSchema` (JSON Schema from the parameters; `CancellationToken` and the injected services are not part of it), `outputSchema` (JSON Schema from the return type) and `annotations` (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`).
- `tools/call` returns the result twice: `structuredContent` (a JSON object matching the output schema, for programs) and a text block with the same JSON (for the clients that only read text).
- The annotations are **hints** for the hosts (VS Code asks before running a tool that is not read-only), never a security control: authorization is enforced by the server (scenario 3).
- `[McpServerToolType]` classes are created **for each call**, with their constructor parameters taken from dependency injection: here the typed `OrdersApiClient`, itself created by `IHttpClientFactory` (pooled connections, no socket exhaustion).

### 3. Errors: tool errors, not protocol errors

The MCP specification distinguishes two kinds of errors:

| Kind | When | What the client gets |
|------|------|----------------------|
| **Tool error** (`isError: true` in the result) | The tool ran and failed: not found, business rule, invalid argument, downstream outage | A normal result, given to the model, which can explain it or retry differently (scenario 2 of the client lab: the agent orders the 8 units left) |
| **Protocol error** (JSON-RPC error) | The request itself is wrong: unknown tool, forbidden tool, malformed request | An exception in the client (`McpException`), not shown to the model |

The call-tool filter of TODO 7 is the single place where the REST errors become tool errors:

| REST answer | Tool error message | Why |
|-------------|--------------------|-----|
| 400 / 404 / 409 / 422 | The `detail` of the problem details of the microservice | Written for its callers, useful to the model (`Only 8 unit(s) of KB-200 ... left in stock`) |
| 401 / 403 | "The orders service refused the request of the MCP server..." | A configuration problem of the provider (its API key), the client cannot fix it |
| 5xx, timeout, connection refused | "The orders service is unavailable. Try again in a few minutes." | **Never** forward internal details (the microservice logged `orders-db-03.internal:5432`): they are in the log of the MCP server only |
| `McpException` thrown by a tool | Its message | Argument validation (`'../admin' is not a valid SKU...`) |
| Any other exception | "The tool failed unexpectedly." | A bug of the MCP server: logged as an error |

Without the filter, the SDK returns `An error occurred invoking 'get_order'.` for any exception other than `McpException`: safe, but useless for the model.

### 4. Security: the MCP server is an OAuth protected resource

The [MCP authorization specification](https://modelcontextprotocol.io/specification/latest/basic/authorization) makes a remote MCP server an **OAuth 2.1 resource server**:

1. **No token, no tools.** `MapMcp(...).RequireAuthorization(...)`: a request without a valid token gets `401` with `WWW-Authenticate: Bearer resource_metadata="http://localhost:5310/.well-known/oauth-protected-resource/mcp"` (`AddMcp()`).
2. **Protected Resource Metadata (RFC 9728).** That document tells the client which **authorization server** issues tokens for this MCP server and which **scopes** exist. MCP hosts (VS Code, Claude, ChatGPT...) use it to start the OAuth flow by themselves.
3. **Audience.** The token must have been issued **for this MCP server** (`aud` = `http://localhost:5310/mcp`, the `resource` parameter of RFC 8707 sent by the client). A token issued for another API is refused.
4. **Scopes and per-tool authorization.** `orders.read` to use the endpoint, `orders.write` for `place_order`. With `AddAuthorizationFilters()`, a tool whose `[Authorize]` policy fails is **absent from `tools/list`** and **refused in `tools/call`**: the `reporting-agent` cannot order, whatever its agent decides or whatever a prompt injection makes it try.
5. **No token passthrough.** The MCP server never forwards the client's token to the microservice (the specification forbids it: the microservice would accept a token not issued for it, and the audit would be lost). It calls with **its own** credential (`X-Api-Key` of `orders-mcp-server`), and tells on whose behalf in `X-On-Behalf-Of`: the order of scenario 3 is `placedBy: "orders-mcp-server on behalf of sales-agent"`.
6. **Least exposure.** Only 4 endpoints; arguments validated (formats, bounds) before any REST call; the personal-data endpoint not exposed at all.

**The authorization server of the lab is a stand-in.** `Auth/DevAuthorizationServer.cs` issues tokens with the **client credentials** grant (a machine client: the agent of another team) so that the lab runs without setup. In production, register the MCP server and each consumer in **Microsoft Entra ID** (or your identity provider), set `AuthorizationServers` to its issuer, and validate its tokens with `AddJwtBearer(options => { options.Authority = ...; options.Audience = ...; })`: the MCP server never issues tokens. For MCP hosts used by people (VS Code, Claude Desktop), the flow is the **authorization code** grant with PKCE, started by the host from the Protected Resource Metadata.

### 5. Deployment: an independent service

| Concern | In this lab |
|---------|-------------|
| Transport | Streamable HTTP, one endpoint (`/mcp`), **stateless**: no MCP session, any instance can answer any request, no sticky sessions behind a load balancer |
| Configuration | `appsettings.json` overridden by environment variables: `OrdersMcpServer__BaseUrl` (public URL = token audience), `OrdersMcpServer__ListenUrls` (bind address in a container), `OrdersApi__BaseUrl`, `OrdersApi__ApiKey` (a secret) |
| Health | `GET /health` (anonymous): `Healthy`, or `Degraded` when the microservice does not answer |
| Containers | `deploy/OrdersApi.Dockerfile`, `deploy/McpServer.Dockerfile`, `deploy/docker-compose.yml`: only the MCP server is published; the microservice stays on the internal network |
| HTTPS | Terminated by the platform (Azure Container Apps ingress, App Gateway, Kubernetes ingress). Never expose a remote MCP server in plain HTTP outside localhost |

Run the provider side in containers (from the root of the repository):

```bash
docker compose -f LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/deploy/docker-compose.yml up --build
# then, in another terminal, the consumer lab:
cd LearningLabs/AzureOpenAI/Lab16_RestApiMCPClient/Solution && dotnet run
# stop:
docker compose -f LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/deploy/docker-compose.yml down
```

To build your `Start` instead of the `Solution`: add `args: { PROJECT: Start }` under `build:` of `orders-mcp-server`.

On Azure, the same image runs on **Azure Container Apps** (ingress HTTPS, secrets for `OrdersApi__ApiKey`, managed identity, scale rules) or **App Service**. **Azure API Management** can also turn a REST API into an MCP server without code ("Expose REST API as MCP server"): a good option for simple read endpoints; write your own server, as in this lab, when you need curated descriptions, validation, error translation or business rules.

## Use the server from MCP Inspector or VS Code

Start the server with `dotnet run -- --serve`, then get a token (valid 15 minutes, until the server stops):

```bash
curl -s -X POST http://localhost:5310/oauth/token \
  -d grant_type=client_credentials -d client_id=sales-agent -d client_secret=dev-sales-agent-secret \
  -d resource=http://localhost:5310/mcp
```

| Host | Configuration |
|------|---------------|
| [MCP Inspector](https://modelcontextprotocol.io/docs/tools/inspector): `npx @modelcontextprotocol/inspector` | Transport **Streamable HTTP**, URL `http://localhost:5310/mcp`, *Authentication* → header `Authorization`, value `Bearer <access_token>` |
| VS Code (GitHub Copilot, agent mode): `.vscode/mcp.json` | `{ "inputs": [{ "id": "token", "type": "promptString", "password": true, "description": "Orders MCP token" }], "servers": { "orders": { "type": "http", "url": "http://localhost:5310/mcp", "headers": { "Authorization": "Bearer ${input:token}" } } } }` |

Try `place_order` with a `reporting-agent` token: the tool is not even listed.

## Key Concepts

| Concept | Description |
|---------|-------------|
| Remote MCP server | An MCP server reached over HTTP (Streamable HTTP), deployed as a service, as opposed to a local stdio server started by the host |
| `[McpServerToolType]` / `[McpServerTool]` | A class of tools and its tool methods; `WithTools<T>()` registers them; the class is created per call with DI |
| `UseStructuredContent` | Publishes an `outputSchema` and returns `structuredContent` |
| Tool annotations | `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld`: hints for the hosts, not security |
| `McpException` | Thrown by a tool: becomes a tool error with its message |
| `WithRequestFilters` / `AddCallToolFilter` | Middleware around every `tools/call`: here, REST errors → tool errors |
| `AddMcp()` / `ProtectedResourceMetadata` | MCP authentication scheme: the `401` challenge points to the metadata (authorization servers, scopes) |
| `AddAuthorizationFilters()` + `[Authorize]` | Per-tool authorization: hidden from `tools/list`, refused in `tools/call` |
| Token audience (RFC 8707) | A token is valid for one resource: the MCP server refuses the tokens issued for others, and never forwards its clients' tokens |
| Stateless Streamable HTTP | No MCP session: horizontal scaling without session affinity |

## Namespaces Reference

| Namespace | Purpose |
|-----------|---------|
| `ModelContextProtocol.Server` | `McpServerToolType`, `McpServerTool` attributes |
| `ModelContextProtocol` | `McpException`, `McpProtocolException` |
| `ModelContextProtocol.Protocol` | `CallToolResult`, `TextContentBlock`, `Implementation` |
| `ModelContextProtocol.AspNetCore.Authentication` | `McpAuthenticationDefaults` |
| `ModelContextProtocol.Authentication` | `ProtectedResourceMetadata` |
| `ModelContextProtocol.Client` | `McpClient`, `HttpClientTransport`, `McpClientTool` (scenarios) |
| `Microsoft.Extensions.DependencyInjection` | `AddMcpServer`, `WithHttpTransport`, `WithTools`, `AddAuthorizationFilters`, `WithRequestFilters`, `AddHttpClient`, `AddHealthChecks` |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | `JwtBearerDefaults`, `AddJwtBearer` |
| `Microsoft.AspNetCore.Authorization` | `[Authorize]` |
| `Microsoft.AspNetCore.Diagnostics.HealthChecks` | `HealthCheckOptions` |
| `System.ComponentModel` | `[Description]` |

## Expected Output

```
REST microservice: OrdersApi started as a separate process (PID 12345) at http://localhost:5300
MCP server listening on http://localhost:5310/mcp (Streamable HTTP, stateless, bearer token required)
----------------------------------------
=== Scenario 1: Discover and call the tools exposed from the REST API ===
Connected to Orders MCP server 1.0.0 as sales-agent
MCP tools available (4): get_order, get_product, place_order, search_products
- get_order (Get an order) [read-only, closed world]
  Description: Gets an order by its id: status (Pending, Shipped, Cancelled), lines, total in euros and date.
  Input schema: {"type":"object","properties":{"orderId":{"description":"Id of the order, e.g. ORD-1002.","type":"string"}},"required":["orderId"]}
  Output schema: {"description":"An order of a customer.","type":"object","properties":{"orderId":{...
- place_order (Place an order) [closed world]
  ...
search_products(category: keyboards, maxPrice: 100) -> result:
  Structured content: {"count":2,"products":[{"sku":"KB-100","name":"Compact mechanical keyboard",...,"stock":25},{"sku":"KB-300",...,"stock":0}]}
get_order(orderId: ORD-1002) -> result:
  Structured content: {"orderId":"ORD-1002","customerId":"CUST-042","status":"Pending",...,"total":114.80,...}
----------------------------------------
=== Scenario 2: Pass the REST errors to the MCP client ===
get_order(orderId: ORD-9999) -> tool error (isError: true):
  Order ORD-9999 does not exist.
place_order(CUST-042, KB-200 x 30) -> tool error (isError: true):
  Only 8 unit(s) of KB-200 (Wireless ergonomic keyboard) left in stock, 30 requested.
warn: RestApiMCPServer.Tools[0] Tool get_order failed: OrdersApi answered 500: An error occurred while processing your request.
get_order(orderId: ORD-0500) -> tool error (isError: true):
  The orders service is unavailable. Try again in a few minutes.
  Internal details sent to the client: no
get_product(sku: ../admin) -> tool error (isError: true):
  '../admin' is not a valid SKU. A SKU looks like KB-100: two letters, a dash and three digits.
----------------------------------------
=== Scenario 3: Protect the tools with a token, scopes and per-tool authorization ===
Without a token: 401 Unauthorized
  WWW-Authenticate: Bearer resource_metadata="http://localhost:5310/.well-known/oauth-protected-resource/mcp"
Protected Resource Metadata (http://localhost:5310/.well-known/oauth-protected-resource/mcp):
  resource: http://localhost:5310/mcp, authorization_servers: ["http://localhost:5310"], scopes_supported: ["orders.read","orders.write"]
reporting-agent (scope orders.read):
MCP tools available (3): get_order, get_product, search_products
place_order as reporting-agent -> refused: Request failed (remote): Access forbidden: This tool requires authorization.
sales-agent (scopes orders.read orders.write):
MCP tools available (4): get_order, get_product, place_order, search_products
place_order(CUST-042, MS-100 x 2) as sales-agent -> result:
  Structured content: {"orderId":"ORD-1004",...,"placedBy":"orders-mcp-server on behalf of sales-agent"}
----------------------------------------
=== Scenario 4: Run the MCP server as an independent service ===
GET /health: 200 {"status":"Healthy","service":"orders-mcp-server","checks":{"orders-api":{"status":"Healthy","Description":"OrdersApi answers."}}}
Two independent connections answered: yes (MCP session id: none)
Deployment settings (environment variables):
  ...
```

The `warn:` lines are the log of the MCP server (its tool errors): the details stay on the server side. The microservice keeps its data in memory: restart it (or the lab, when the lab started it) to get the initial stock back.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `OrdersApi stopped (exit code 1) before it was ready` | Port 5300 used by another program, or a build error of `OrdersApi`: run `cd ../OrdersApi && dotnet run` to see it |
| `Failed to bind to address http://127.0.0.1:5310` | Another MCP server (`--serve`, docker compose) runs: stop it, or set `OrdersMcpServer__BaseUrl` |
| Every tool call fails with `The orders service refused the request of the MCP server` | `OrdersApi:ApiKey` of the MCP server is not one of the `OrdersApi:ApiKeys` of the microservice |
| `The orders service is unavailable` on every call | The microservice is stopped (`/health` of the MCP server says `Degraded`) or `OrdersApi:BaseUrl` is wrong |
| `401` with a token | Token expired (15 minutes), issued by a server that restarted (the signing key is random per run), or requested for another `resource` |
| `place_order` missing from the tools | The token has no `orders.write` scope (client `reporting-agent`), or `AddAuthorizationFilters()` with a wrong policy name |
| No `Output schema` | `UseStructuredContent = true` is missing on the tool |
| `An error occurred invoking 'get_order'.` | The call-tool filter (TODO 7) is missing or does not catch `OrdersApiException` |
| In Docker, `401` from the client lab | `OrdersMcpServer__BaseUrl` must be the URL the clients use (`http://localhost:5310`): it is the audience of the tokens |

## Going further

- **Real identity provider**: Microsoft Entra ID app registrations for the MCP server (expose the scopes `orders.read`, `orders.write` as app roles) and for each consumer (client credentials or managed identity).
- **Rate limiting** per client: `builder.Services.AddRateLimiter(...)` with a partition on `client_id`, `MapMcp(...).RequireRateLimiting(...)`.
- **Resilience** of the REST calls: `Microsoft.Extensions.Http.Resilience` (`AddStandardResilienceHandler()`) for retries of idempotent reads and a circuit breaker.
- **User identity** (not just the client): OAuth token exchange / on-behalf-of, so that the microservice applies the permissions of the end user.
- **Generate tools from OpenAPI**: possible for large APIs, but review the result: an endpoint is rarely a good tool as is (names, descriptions, too many parameters).
- **Resources and prompts**: an MCP server can also expose read-only data as resources (`WithResources`) and reusable prompts (`WithPrompts`).

## Security

- Secrets of the lab (`OrdersApi:ApiKey`, client secrets, API keys of the microservice) are **development values** in `appsettings.json`: in a real service, use a vault or the secrets of the platform, and never commit them.
- The development authorization server signs with a random key per run and accepts any registered client: it is a stand-in, never deploy it.
- The HTTP server listens on `localhost` without HTTPS: enough for the lab only.

## Provided Files

The following files are provided and should not be modified: `OrdersApi/`, `RestClient/`, `Auth/`, `Hosting/`, `McpConsole.cs`, `Settings.cs`, `ConfigurationHelper.cs`, `deploy/`.
