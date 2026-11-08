using Microsoft.Extensions.Configuration;

namespace RestApiMCPServer;

/// <summary>
/// Helper class for loading configuration.
/// Sources, from lowest to highest priority: appsettings.json, user secrets, environment variables
/// (e.g. <c>OrdersApi__BaseUrl</c>, <c>OrdersApi__ApiKey</c>, <c>OrdersMcpServer__BaseUrl</c>): what a container platform sets.
/// No Azure OpenAI settings: the MCP server has no model.
/// </summary>
public static class ConfigurationHelper
{
    private static IConfiguration? _configuration;

    public static IConfiguration Configuration => _configuration ??= new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddUserSecrets<OrdersApiSettings>(optional: true)
        .AddEnvironmentVariables()
        .Build();

    public static OrdersApiSettings GetOrdersApiSettings()
    {
        OrdersApiSettings settings = Configuration.GetSection("OrdersApi").Get<OrdersApiSettings>()
            ?? throw new InvalidOperationException("OrdersApi configuration section is missing.");
        settings.BaseUrl = EnsureHttpUrl(settings.BaseUrl, "OrdersApi:BaseUrl");
        EnsureConfigured(settings.ApiKey, "OrdersApi:ApiKey");
        return settings;
    }

    public static McpServerSettings GetMcpServerSettings()
    {
        McpServerSettings settings = Configuration.GetSection("OrdersMcpServer").Get<McpServerSettings>()
            ?? throw new InvalidOperationException("OrdersMcpServer configuration section is missing.");
        settings.BaseUrl = EnsureHttpUrl(settings.BaseUrl, "OrdersMcpServer:BaseUrl");
        return settings;
    }

    public static IReadOnlyDictionary<string, DevClientSettings> GetDevClients() =>
        Configuration.GetSection("DevAuthorization:Clients").Get<Dictionary<string, DevClientSettings>>() ?? [];

    private static string EnsureHttpUrl(string value, string key)
    {
        EnsureConfigured(value, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"'{key}' must be an absolute http:// or https:// URL.");
        }

        return value.TrimEnd('/');
    }

    private static void EnsureConfigured(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"'{key}' is not configured. Set it in appsettings.json or with the environment variable '{key.Replace(":", "__")}'.");
        }
    }
}
