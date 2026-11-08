namespace RestApiMCPClient;

/// <summary>The remote MCP server of the provider team, as the consumer team knows it (section "RemoteMcpServer").</summary>
public class RemoteMcpServerSettings
{
    /// <summary>URL of the MCP endpoint, e.g. http://localhost:5310/mcp. Also the resource the tokens are requested for.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Token endpoint of the authorization server (in production: Microsoft Entra ID).</summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>The credentials the provider team gave to the consumer team, one per client (SalesAgent, ReportingAgent).</summary>
    public Dictionary<string, ClientCredentials> Clients { get; set; } = [];
}

public class ClientCredentials
{
    public string ClientId { get; set; } = string.Empty;

    /// <summary>A secret: user secrets, environment variable or vault in a real application, never in source control.</summary>
    public string ClientSecret { get; set; } = string.Empty;
}
