namespace RestApiMCPClient;

// ============================================
// PROVIDED - do not edit
// ============================================
// The names, instructions and questions of the agents of this lab.
// They are given so that everyone works with the same agents and gets comparable answers:
// use them in Program.cs instead of writing your own texts, and focus on the Agent Framework and MCP code.

/// <summary>The agents of the consumer team: the name and the instructions to give to <c>AsAIAgent(...)</c>.</summary>
public static class Agents
{
    /// <summary>Scenarios 1 and 2: the sales assistant, client "sales-agent" (scopes orders.read and orders.write).</summary>
    public static class SalesAssistant
    {
        public const string Name = "SalesAssistant";

        public const string Instructions =
            "You are the sales assistant of an online shop. Use the tools to search the catalog, read orders and place orders. " +
            "Answer only with facts returned by the tools; prices are in euros. If a tool returns an error, explain it to the user " +
            "and, when the error says how many units are left, adapt the request instead of giving up.";
    }

    /// <summary>Scenario 3: the reporting assistant, client "reporting-agent" (scope orders.read only).</summary>
    public static class ReportingAssistant
    {
        public const string Name = "ReportingAssistant";

        public const string Instructions =
            "You are a reporting assistant of an online shop, with a read-only access: use the tools to answer questions about " +
            "the catalog and the orders. If you are asked to place, change or cancel an order, answer that your access does not " +
            "allow it, without calling any tool for that part of the request.";
    }
}

/// <summary>The questions asked to the agents.</summary>
public static class Questions
{
    /// <summary>Scenario 1: two read tools (search_products, get_order).</summary>
    public const string KeyboardsAndOrder =
        "Which keyboards cost less than 100 euros and are in stock? And what is the status of the order ORD-1002?";

    /// <summary>Scenario 2: a write tool and a REST error (409, not enough stock) the agent must handle.</summary>
    public const string OrderTenKeyboards =
        "Place an order of 10 units of KB-200 for the customer CUST-042 directly, without checking the stock first. " +
        "If the order fails because there is not enough stock, order all the units that are left. " +
        "Then give me the order id, the quantity ordered and the total.";

    /// <summary>Scenario 2: an unknown order (404).</summary>
    public const string UnknownOrder = "What is the status of the order ORD-9999?";

    /// <summary>Scenario 3: a read question, then a request the read-only client may not do.</summary>
    public const string StockAndOrder =
        "How many 27-inch monitors (MN-270) are in stock? Also, place an order of one MN-270 for the customer CUST-077.";
}
