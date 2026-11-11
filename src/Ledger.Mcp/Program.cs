using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Thin MCP stdio bridge to Ledger's localhost Grokbot API.
// Env: LEDGER_API_URL (default http://127.0.0.1:17865/), LEDGER_API_TOKEN (required).

var baseUrl = (Environment.GetEnvironmentVariable("LEDGER_API_URL") ?? "http://127.0.0.1:17865/").TrimEnd('/') + "/";
var token = Environment.GetEnvironmentVariable("LEDGER_API_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    await Console.Error.WriteLineAsync("LEDGER_API_TOKEN is required. Copy it from Ledger → Settings → Grokbot.");
    return 1;
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

var jsonOptions = new JsonSerializerOptions { WriteIndented = false };

await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();
using var reader = new StreamReader(stdin, Encoding.UTF8);
await using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };

while (await reader.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonNode? response = null;
    try
    {
        var req = JsonNode.Parse(line)!.AsObject();
        var method = req["method"]?.GetValue<string>();
        var id = req["id"];

        if (method is "notifications/initialized" or "notifications/cancelled")
        {
            continue;
        }

        response = method switch
        {
            "initialize" => Ok(id, new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "ledger", ["version"] = "1.0.0" },
            }),
            "ping" => Ok(id, new JsonObject()),
            "tools/list" => Ok(id, await ListToolsAsync(http)),
            "tools/call" => Ok(id, await CallToolAsync(http, req["params"]?.AsObject())),
            _ => Error(id, -32601, $"Method not found: {method}"),
        };
    }
    catch (Exception ex)
    {
        response = Error(null, -32603, ex.Message);
    }

    if (response is not null)
    {
        await writer.WriteLineAsync(response.ToJsonString(jsonOptions));
    }
}

return 0;

static JsonObject Ok(JsonNode? id, JsonNode result) => new()
{
    ["jsonrpc"] = "2.0",
    ["id"] = id?.DeepClone(),
    ["result"] = result,
};

static JsonObject Error(JsonNode? id, int code, string message) => new()
{
    ["jsonrpc"] = "2.0",
    ["id"] = id?.DeepClone(),
    ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
};

static async Task<JsonObject> ListToolsAsync(HttpClient http)
{
    using var res = await http.GetAsync("v1/tools");
    var body = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(Explain(res.StatusCode, body));
    }

    var tools = JsonNode.Parse(body)!["tools"]!.AsArray();
    var list = new JsonArray();
    foreach (var t in tools)
    {
        list.Add(new JsonObject
        {
            ["name"] = t!["name"]!.GetValue<string>(),
            ["description"] = t["description"]?.GetValue<string>() ?? "",
            ["inputSchema"] = t["input_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        });
    }

    list.Add(new JsonObject
    {
        ["name"] = "get_proposal",
        ["description"] = "Poll a write proposal until it is approved, denied, expired or failed.",
        ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}"""),
    });
    list.Add(new JsonObject
    {
        ["name"] = "list_pending_proposals",
        ["description"] = "List write proposals waiting for Approve in Ledger.",
        ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
    });

    return new JsonObject { ["tools"] = list };
}

static async Task<JsonObject> CallToolAsync(HttpClient http, JsonObject? args)
{
    var name = args?["name"]?.GetValue<string>()
        ?? throw new InvalidOperationException("tools/call requires params.name");
    var arguments = args?["arguments"]?.AsObject() ?? [];

    if (name == "get_proposal")
    {
        var id = arguments["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("id is required");
        using var res = await http.GetAsync($"v1/proposals/{Uri.EscapeDataString(id)}");
        var body = await res.Content.ReadAsStringAsync();
        return ToolResult(body, res.IsSuccessStatusCode);
    }

    if (name == "list_pending_proposals")
    {
        using var res = await http.GetAsync("v1/proposals");
        var body = await res.Content.ReadAsStringAsync();
        return ToolResult(body, res.IsSuccessStatusCode);
    }

    using (var content = new StringContent(arguments.ToJsonString(), Encoding.UTF8, "application/json"))
    using (var res = await http.PostAsync($"v1/tools/{Uri.EscapeDataString(name)}", content))
    {
        var body = await res.Content.ReadAsStringAsync();
        return ToolResult(body, res.IsSuccessStatusCode || (int)res.StatusCode == 202);
    }
}

static JsonObject ToolResult(string body, bool ok) => new()
{
    ["content"] = new JsonArray
    {
        new JsonObject { ["type"] = "text", ["text"] = body },
    },
    ["isError"] = !ok,
};

static string Explain(System.Net.HttpStatusCode status, string body) => status switch
{
    System.Net.HttpStatusCode.Unauthorized => "Unauthorized. Check LEDGER_API_TOKEN matches Settings → Grokbot.",
    System.Net.HttpStatusCode.NotFound => "Ledger API not found. Is Ledger running with Grokbot access on?",
    _ when body.Contains("error", StringComparison.OrdinalIgnoreCase) => body,
    _ => $"Ledger API returned {(int)status}. Is Ledger running with Grokbot access on? {body}",
};
