using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RestApiMCPServer.Auth;
using RestApiMCPServer.RestClient;

namespace RestApiMCPServer.Tools;

/// <summary>
/// Order tools: GET /api/orders/{orderId} and POST /api/orders. The other order endpoints of the microservice
/// (cancel, delete) and the customer endpoint (personal data) are deliberately NOT exposed: the MCP server is a curated
/// facade, not a proxy of the whole API.
/// </summary>
[McpServerToolType]
public sealed partial class OrderTools(OrdersApiClient ordersApi)
{
    // Solution of TODO 3: GET /api/orders/{orderId}
    [McpServerTool(Name = "get_order", Title = "Get an order", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets an order by its id: status (Pending, Shipped, Cancelled), lines, total in euros and date.")]
    public async Task<Order> GetOrder(
        [Description("Id of the order, e.g. ORD-1002.")] string orderId,
        CancellationToken cancellationToken = default)
    {
        if (!OrderIdFormat().IsMatch(orderId))
        {
            throw new McpException($"'{orderId}' is not a valid order id. An order id looks like ORD-1002.");
        }

        return await ordersApi.GetOrderAsync(orderId.ToUpperInvariant(), cancellationToken);
    }

    // Solution of TODO 4: POST /api/orders, a tool that WRITES. ReadOnly = false, Destructive = false (it adds an order, it deletes nothing),
    // Idempotent = false (two calls place two orders): hosts such as VS Code ask the user before running it.
    // [Authorize(Policy = ...)]: only the clients whose token has the orders.write scope see this tool and may call it
    // (enforced by AddAuthorizationFilters() in Program.cs, whatever the agent of the client decides).
    [McpServerTool(Name = "place_order", Title = "Place an order", ReadOnly = false, Destructive = false, Idempotent = false,
        OpenWorld = false, UseStructuredContent = true)]
    [Description("Places an order for a customer. The stock is reserved and the order is created with the status Pending. " +
                 "Fails if a product does not exist or if there is not enough stock; the error says how many units are left.")]
    [Authorize(Policy = DevAuthorizationServer.WriteScope)]
    public async Task<Order> PlaceOrder(
        [Description("Id of the customer, e.g. CUST-042.")] string customerId,
        [Description("The lines of the order: 1 to 10 lines, each with a SKU and a quantity from 1 to 50.")] OrderLineRequest[] lines,
        CancellationToken cancellationToken = default)
    {
        if (lines.Length is < 1 or > 10)
        {
            throw new McpException("An order has 1 to 10 lines.");
        }

        return await ordersApi.PlaceOrderAsync(new PlaceOrderRequest(customerId.Trim().ToUpperInvariant(), lines), cancellationToken);
    }

    [GeneratedRegex("^[Oo][Rr][Dd]-[0-9]{4}$")]
    private static partial Regex OrderIdFormat();
}
