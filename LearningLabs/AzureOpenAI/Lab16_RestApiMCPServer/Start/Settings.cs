namespace RestApiMCPServer;

/// <summary>How the MCP server reaches the REST microservice (section "OrdersApi").</summary>
public class OrdersApiSettings
{
    /// <summary>Address of the microservice, e.g. http://localhost:5300 (in docker compose: http://orders-api:8080).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The API key of the MCP server itself, a client of the microservice like any other. Never the token of an MCP client.
    /// Keep it in user secrets, an environment variable (OrdersApi__ApiKey) or a vault in production.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Timeout of a call to the microservice: a slow REST API must not block an agent for minutes.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Lab convenience: start OrdersApi (dotnet run) when it is not running. False in containers.</summary>
    public bool StartIfNotRunning { get; set; } = true;
}

/// <summary>Where the MCP server listens (section "OrdersMcpServer").</summary>
public class McpServerSettings
{
    /// <summary>Address the MCP server listens on, e.g. http://localhost:5310. The MCP endpoint is {BaseUrl}/mcp.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional: the addresses the server binds to, when they differ from the public <see cref="BaseUrl"/>.
    /// In a container: http://+:8080, while the clients use the published address (BaseUrl, e.g. https://orders-mcp.contoso.com).
    /// </summary>
    public string? ListenUrls { get; set; }

    /// <summary>True (or --serve): skip the scenarios and serve MCP clients until Ctrl+C.</summary>
    public bool Serve { get; set; }
}

/// <summary>A client registered on the development authorization server (section "DevAuthorization:Clients:{clientId}").</summary>
public class DevClientSettings
{
    public string Secret { get; set; } = string.Empty;

    /// <summary>The scopes the client may get, separated by spaces (orders.read, orders.write).</summary>
    public string Scopes { get; set; } = string.Empty;
}
