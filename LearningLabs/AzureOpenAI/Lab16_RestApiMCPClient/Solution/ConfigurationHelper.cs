using Microsoft.Extensions.Configuration;

namespace RestApiMCPClient;

/// <summary>
/// Helper class for loading configuration.
/// Sources, from lowest to highest priority: appsettings.json, user secrets, environment variables
/// (e.g. <c>AzureOpenAI__APIKey</c>, <c>RemoteMcpServer__Url</c>).
/// </summary>
public static class ConfigurationHelper
{
    private static IConfiguration? _configuration;

    public static IConfiguration Configuration => _configuration ??= new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddUserSecrets<AzureOpenAISettings>(optional: true)
        .AddEnvironmentVariables()
        .Build();

    /// <summary>
    /// Get the Azure OpenAI settings from the configuration.
    /// </summary>
    public static AzureOpenAISettings GetAzureOpenAISettings()
    {
        AzureOpenAISettings settings = Configuration.GetSection("AzureOpenAI").Get<AzureOpenAISettings>()
            ?? throw new InvalidOperationException("AzureOpenAI configuration section is missing.");

        EnsureConfigured(settings.Endpoint, "AzureOpenAI:Endpoint");
        EnsureConfigured(settings.ChatDeploymentName, "AzureOpenAI:ChatDeploymentName");

        return settings;
    }

    /// <summary>
    /// Get the settings of the remote MCP server of the provider team: its URL, its token endpoint and the client credentials.
    /// </summary>
    public static RemoteMcpServerSettings GetRemoteMcpServerSettings()
    {
        RemoteMcpServerSettings settings = Configuration.GetSection("RemoteMcpServer").Get<RemoteMcpServerSettings>()
            ?? throw new InvalidOperationException("RemoteMcpServer configuration section is missing.");

        EnsureHttpUrl(settings.Url, "RemoteMcpServer:Url");
        EnsureHttpUrl(settings.TokenEndpoint, "RemoteMcpServer:TokenEndpoint");
        foreach (string client in new[] { "SalesAgent", "ReportingAgent" })
        {
            if (!settings.Clients.TryGetValue(client, out ClientCredentials? credentials))
            {
                throw new InvalidOperationException($"'RemoteMcpServer:Clients:{client}' is missing.");
            }

            EnsureConfigured(credentials.ClientId, $"RemoteMcpServer:Clients:{client}:ClientId");
            EnsureConfigured(credentials.ClientSecret, $"RemoteMcpServer:Clients:{client}:ClientSecret");
        }

        settings.Url = settings.Url.TrimEnd('/');
        return settings;
    }

    private static void EnsureHttpUrl(string value, string key)
    {
        EnsureConfigured(value, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"'{key}' must be an absolute http:// or https:// URL.");
        }
    }

    private static void EnsureConfigured(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("YOUR-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{key}' is not configured. Set it in appsettings.json, with 'dotnet user-secrets set \"{key}\" <value>', " +
                $"or with the environment variable '{key.Replace(":", "__")}'.");
        }
    }
}
