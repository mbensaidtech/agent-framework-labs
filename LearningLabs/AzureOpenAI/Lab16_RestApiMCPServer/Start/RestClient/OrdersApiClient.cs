using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace RestApiMCPServer.RestClient;

// ============================================
// PROVIDED - do not edit
// ============================================

/// <summary>
/// Typed HTTP client of the REST microservice (OrdersApi): one method per endpoint the MCP server uses.
/// The base address, the API key header and the timeout are set where the client is registered
/// (<c>builder.Services.AddHttpClient&lt;OrdersApiClient&gt;(...)</c> in Program.cs): this class only builds the requests.
/// A non-success answer, a connection failure or a timeout becomes an <see cref="OrdersApiException"/>.
/// </summary>
public sealed class OrdersApiClient(HttpClient http, IHttpContextAccessor httpContextAccessor)
{
    /// <summary>The header the microservice records with the orders: on whose behalf the MCP server calls (the MCP client).</summary>
    public const string OnBehalfOfHeader = "X-On-Behalf-Of";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>GET /api/products?category=&amp;maxPrice=</summary>
    public async Task<IReadOnlyList<Product>> SearchProductsAsync(string? category, decimal? maxPrice, CancellationToken cancellationToken)
    {
        List<string> query = [];
        if (!string.IsNullOrWhiteSpace(category)) query.Add($"category={Uri.EscapeDataString(category)}");
        if (maxPrice is not null) query.Add($"maxPrice={maxPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        string url = query.Count == 0 ? "api/products" : $"api/products?{string.Join('&', query)}";
        return await SendAsync<List<Product>>(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
    }

    /// <summary>GET /api/products/{sku}</summary>
    public Task<Product> GetProductAsync(string sku, CancellationToken cancellationToken) =>
        SendAsync<Product>(new HttpRequestMessage(HttpMethod.Get, $"api/products/{Uri.EscapeDataString(sku)}"), cancellationToken);

    /// <summary>GET /api/orders/{orderId}</summary>
    public Task<Order> GetOrderAsync(string orderId, CancellationToken cancellationToken) =>
        SendAsync<Order>(new HttpRequestMessage(HttpMethod.Get, $"api/orders/{Uri.EscapeDataString(orderId)}"), cancellationToken);

    /// <summary>POST /api/orders</summary>
    public Task<Order> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken cancellationToken) =>
        SendAsync<Order>(new HttpRequestMessage(HttpMethod.Post, "api/orders") { Content = JsonContent.Create(request, options: Json) }, cancellationToken);

    /// <summary>GET /health (anonymous): used by the health check of the MCP server.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Audit: the MCP server calls with its own API key, and says which MCP client it acts for (client_id of the token)
        string? client = httpContextAccessor.HttpContext?.User.FindFirstValue("client_id");
        if (!string.IsNullOrEmpty(client))
        {
            request.Headers.Add(OnBehalfOfHeader, client);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OrdersApiException(null, "Connection failed", ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrdersApiException(null, "Timeout", "The microservice did not answer in time.", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
            }

            // Problem details (RFC 9457): title, detail, and "errors" for a validation problem
            string title = response.ReasonPhrase ?? "Error";
            string? detail = null;
            try
            {
                JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
                if (problem.ValueKind == JsonValueKind.Object)
                {
                    if (problem.TryGetProperty("title", out JsonElement t)) title = t.GetString() ?? title;
                    if (problem.TryGetProperty("detail", out JsonElement d)) detail = d.GetString();
                    if (problem.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
                    {
                        detail = string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(m => m.GetString())));
                    }
                }
            }
            catch (JsonException)
            {
                // Not a problem details body: the status code says enough
            }

            throw new OrdersApiException((int)response.StatusCode, title, detail);
        }
    }
}
