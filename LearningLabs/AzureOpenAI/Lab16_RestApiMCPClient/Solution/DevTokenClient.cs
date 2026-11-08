using System.Net.Http.Json;
using System.Text.Json;

namespace RestApiMCPClient;

// ============================================
// PROVIDED - do not edit
// ============================================

/// <summary>
/// What a machine MCP client does before it connects: the OAuth 2.0 client credentials grant. It sends its client id and secret,
/// the resource it wants a token for (the MCP endpoint, RFC 8707) and the scopes, and receives an access token.
/// In production, use the library of your identity provider (e.g. Azure.Identity ClientSecretCredential or a managed identity).
/// </summary>
public static class DevTokenClient
{
    public static async Task<string> GetAccessTokenAsync(HttpClient http, string tokenEndpoint, string clientId, string clientSecret,
        string resource, string? scope = null, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["resource"] = resource
        };
        if (!string.IsNullOrWhiteSpace(scope))
        {
            form["scope"] = scope;
        }

        using HttpResponseMessage response = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token request refused ({(int)response.StatusCode}): {body}");
        }

        return body.GetProperty("access_token").GetString()!;
    }
}
