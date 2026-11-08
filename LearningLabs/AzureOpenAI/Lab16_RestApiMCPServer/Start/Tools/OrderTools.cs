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
#pragma warning disable CS9113 // ordersApi is used by the tools you write below
[McpServerToolType]
public sealed partial class OrderTools(OrdersApiClient ordersApi)
#pragma warning restore CS9113
{
    // TODO 3: Expose GET /api/orders/{orderId} as the MCP tool "get_order" (Title "Get an order", read-only, closed world,
    // structured content), description "Gets an order by its id: status (Pending, Shipped, Cancelled), lines, total in euros and date."
    // and one parameter: string orderId ("Id of the order, e.g. ORD-1002."), validated with OrderIdFormat() as in TODO 2
    // ("'...' is not a valid order id. An order id looks like ORD-1002.").
    // Hint: return await ordersApi.GetOrderAsync(orderId.ToUpperInvariant(), cancellationToken);

    // TODO 4: Expose POST /api/orders as the MCP tool "place_order": a tool that WRITES.
    // - [McpServerTool(Name = "place_order", Title = "Place an order", ReadOnly = false, Destructive = false, Idempotent = false,
    //   OpenWorld = false, UseStructuredContent = true)]: it adds an order (not destructive), two calls place two orders
    //   (not idempotent): hosts such as VS Code ask the user before running it
    // - [Description("Places an order for a customer. The stock is reserved and the order is created with the status Pending. Fails if
    //   a product does not exist or if there is not enough stock; the error says how many units are left.")]
    // - [Authorize(Policy = DevAuthorizationServer.WriteScope)]: only the clients whose token has the orders.write scope see this
    //   tool and may call it (enforced by AddAuthorizationFilters() in Program.cs)
    // - Parameters: string customerId ("Id of the customer, e.g. CUST-042."), OrderLineRequest[] lines ("The lines of the order:
    //   1 to 10 lines, each with a SKU and a quantity from 1 to 50.")
    // Hint: if (lines.Length is < 1 or > 10) throw new McpException("An order has 1 to 10 lines.");
    //       return await ordersApi.PlaceOrderAsync(new PlaceOrderRequest(customerId.Trim().ToUpperInvariant(), lines), cancellationToken);

    // Provided: the format of an order id
    [GeneratedRegex("^[Oo][Rr][Dd]-[0-9]{4}$")]
    private static partial Regex OrderIdFormat();
}
