using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace RestApiMCPServer.Auth;

// ============================================
// PROVIDED - do not edit
// ============================================

/// <summary>
/// Development authorization server: it stands in for your identity provider (Microsoft Entra ID, Keycloak, Okta...) so that the
/// lab runs without any setup. It implements the OAuth 2.0 <b>client credentials</b> grant, the flow of a machine client such as
/// the agent of another team: the client sends its id and secret to <c>POST /oauth/token</c> and receives a signed access token
/// (JWT) for the MCP server, with the scopes it is allowed (DevAuthorization:Clients in appsettings.json).
/// It is hosted by the MCP server process for convenience only. Never issue tokens yourself in production: register the MCP
/// server and its clients in your identity provider, and only <b>validate</b> its tokens (<see cref="ValidationParameters"/>).
/// </summary>
public sealed class DevAuthorizationServer
{
    public const string ReadScope = "orders.read";
    public const string WriteScope = "orders.write";
    public static readonly string[] Scopes = [ReadScope, WriteScope];

    private readonly SymmetricSecurityKey _signingKey = new(RandomNumberGenerator.GetBytes(32));
    private readonly IReadOnlyDictionary<string, DevClientSettings> _clients;

    /// <param name="issuer">Identifier of the authorization server (here the base URL of the MCP server).</param>
    /// <param name="resource">The protected resource the tokens are issued for: the URL of the MCP endpoint (token audience).</param>
    public DevAuthorizationServer(string issuer, string resource, IReadOnlyDictionary<string, DevClientSettings> clients)
    {
        Issuer = issuer;
        Resource = resource;
        _clients = clients;
    }

    public string Issuer { get; }

    public string Resource { get; }

    public string TokenEndpoint => $"{Issuer}/oauth/token";

    /// <summary>
    /// What the MCP server checks in every token: issued by this authorization server, signed with its key, not expired, and issued
    /// <b>for this MCP server</b> (audience = the MCP endpoint URL). A token issued for another API is refused (no token passthrough).
    /// </summary>
    public TokenValidationParameters ValidationParameters => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Resource,
        IssuerSigningKey = _signingKey,
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    /// <summary>Maps the token endpoint and the authorization server metadata (RFC 8414) on the application.</summary>
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Issuer,
            ["token_endpoint"] = TokenEndpoint,
            ["grant_types_supported"] = new[] { "client_credentials" },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post" },
            ["scopes_supported"] = Scopes
        }));

        app.MapPost("/oauth/token", async (HttpRequest request) =>
        {
            IFormCollection form = await request.ReadFormAsync();
            string clientId = form["client_id"].ToString();
            string clientSecret = form["client_secret"].ToString();

            if (form["grant_type"] != "client_credentials")
            {
                return OAuthError("unsupported_grant_type", "Only the client_credentials grant is supported.");
            }

            if (!_clients.TryGetValue(clientId, out DevClientSettings? client) || !FixedTimeEquals(client.Secret, clientSecret))
            {
                return OAuthError("invalid_client", "Unknown client or wrong secret.", StatusCodes.Status401Unauthorized);
            }

            // RFC 8707: the client names the resource it wants a token for. The MCP specification requires it from MCP clients.
            string resource = form["resource"].ToString();
            if (!string.IsNullOrEmpty(resource) && resource.TrimEnd('/') != Resource)
            {
                return OAuthError("invalid_target", $"This authorization server only issues tokens for {Resource}.");
            }

            // Scopes: the requested ones the client is allowed, or all the scopes it is allowed when it asks for none
            string[] allowed = client.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] requested = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] granted = requested.Length == 0 ? allowed : [.. requested.Intersect(allowed)];
            if (granted.Length == 0)
            {
                return OAuthError("invalid_scope", $"Client {clientId} may only get: {client.Scopes}.");
            }

            const int lifetimeSeconds = 900;
            string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Resource,
                Subject = new ClaimsIdentity([new Claim("sub", clientId), new Claim("client_id", clientId), new Claim("scope", string.Join(' ', granted))]),
                Expires = DateTime.UtcNow.AddSeconds(lifetimeSeconds),
                SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
            });

            return Results.Json(new Dictionary<string, object>
            {
                ["access_token"] = token,
                ["token_type"] = "Bearer",
                ["expires_in"] = lifetimeSeconds,
                ["scope"] = string.Join(' ', granted)
            });
        });
    }

    /// <summary>True when the "scope" claim of the user (space-separated, OAuth 2.0) contains <paramref name="scope"/>.</summary>
    public static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.FindAll("scope").SelectMany(claim => claim.Value.Split(' ')).Contains(scope);

    private static IResult OAuthError(string error, string description, int statusCode = StatusCodes.Status400BadRequest) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, statusCode: statusCode);

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
}
