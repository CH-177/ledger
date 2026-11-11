using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ledger.Core;
using Ledger.Core.Assistant;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.Services;

/// <summary>
/// Loopback-only HTTP API for Grokbot: read tools run immediately; write tools enqueue
/// proposals that must be Approved in Ledger. Off until Settings turns it on.
/// </summary>
public sealed class GrokbotApiHost(
    FinanceTools reads,
    ProposalService proposals,
    GrokbotTokenStore tokens,
    SettingsService settings,
    ILogger<GrokbotApiHost> logger) : IAsyncDisposable
{
    public const int DefaultPort = 17865;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsListening => _listener?.IsListening == true;
    public int Port { get; private set; } = DefaultPort;
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public event EventHandler? StatusChanged;

    public async Task SyncFromSettingsAsync(CancellationToken ct = default)
    {
        var enabled = await settings.GetAsync(SettingKeys.GrokbotEnabled, ct) == "1";
        var portText = await settings.GetAsync(SettingKeys.GrokbotPort, ct);
        var port = int.TryParse(portText, out var p) && p is >= 1024 and <= 65535 ? p : DefaultPort;
        if (enabled)
        {
            await StartAsync(port, persist: false, ct);
        }
        else
        {
            await StopAsync(persistOff: false);
        }
    }

    public Task StartAsync(int port = DefaultPort, CancellationToken ct = default) =>
        StartAsync(port, persist: true, ct);

    public async Task StartAsync(int port, bool persist, CancellationToken ct = default)
    {
        if (IsListening && Port == port)
        {
            return;
        }
        await StopAsync(persistOff: false);

        if (tokens.Load() is null)
        {
            tokens.GenerateAndSave();
        }

        Port = port;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not start Grokbot API on port {Port}", port);
            throw new InvalidOperationException($"Couldn't listen on 127.0.0.1:{port}. Try another port in Settings.", ex);
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        if (persist)
        {
            await settings.SetAsync(SettingKeys.GrokbotPort, port.ToString(), ct);
            await settings.SetAsync(SettingKeys.GrokbotEnabled, "1", ct);
        }
        logger.LogInformation("Grokbot API listening on {Url}", BaseUrl);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync() => StopAsync(persistOff: true);

    public async Task StopAsync(bool persistOff)
    {
        if (_cts is null && _listener is null)
        {
            if (persistOff)
            {
                await settings.SetAsync(SettingKeys.GrokbotEnabled, "0");
            }
            return;
        }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _loop = null;
        if (persistOff)
        {
            await settings.SetAsync(SettingKeys.GrokbotEnabled, "0");
        }
        logger.LogInformation("Grokbot API stopped");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!Authorize(ctx.Request))
            {
                await WriteJsonAsync(ctx.Response, 401, new JsonObject { ["error"] = "Unauthorized. Use Authorization: Bearer <token> from Settings → Grokbot." });
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var method = ctx.Request.HttpMethod.ToUpperInvariant();

            if (method == "GET" && path is "/v1/health" or "/health")
            {
                await WriteJsonAsync(ctx.Response, 200, new JsonObject { ["ok"] = true, ["service"] = "ledger-grokbot" });
                return;
            }

            if (method == "GET" && path == "/v1/tools")
            {
                var tools = FinanceTools.Definitions.Concat(MutationTools.Definitions)
                    .Select(t => new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = JsonNode.Parse(t.InputSchemaJson) });
                await WriteJsonAsync(ctx.Response, 200, new JsonObject { ["tools"] = new JsonArray([.. tools]) });
                return;
            }

            if (method == "GET" && path.StartsWith("/v1/proposals/", StringComparison.Ordinal))
            {
                var id = path["/v1/proposals/".Length..];
                var proposal = proposals.Get(id);
                if (proposal is null)
                {
                    await WriteJsonAsync(ctx.Response, 404, new JsonObject { ["error"] = "Proposal not found." });
                    return;
                }
                await WriteJsonAsync(ctx.Response, 200, ProposalJson(proposal));
                return;
            }

            if (method == "GET" && path == "/v1/proposals")
            {
                await WriteJsonAsync(ctx.Response, 200, new JsonObject
                {
                    ["pending"] = new JsonArray([.. proposals.Pending.Select(ProposalJson)]),
                });
                return;
            }

            if (method == "POST" && path.StartsWith("/v1/tools/", StringComparison.Ordinal))
            {
                var name = path["/v1/tools/".Length..];
                var body = await ReadBodyAsync(ctx.Request);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var input = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : JsonDocument.Parse("{}").RootElement;

                if (FinanceTools.Definitions.Any(d => d.Name == name))
                {
                    var result = await reads.RunAsync(name, input);
                    await WriteRawAsync(ctx.Response, 200, result);
                    return;
                }

                if (MutationTools.Definitions.Any(d => d.Name == name))
                {
                    try
                    {
                        var proposal = await proposals.ProposeAsync(name, input);
                        await WriteJsonAsync(ctx.Response, 202, new JsonObject
                        {
                            ["proposal_id"] = proposal.Id,
                            ["status"] = "pending",
                            ["summary"] = proposal.Summary,
                            ["expires_at"] = proposal.ExpiresAt.ToString("O"),
                            ["message"] = "Waiting for Approve in Ledger. Poll GET /v1/proposals/{id}.",
                        });
                    }
                    catch (LedgerValidationException ex)
                    {
                        await WriteJsonAsync(ctx.Response, 400, new JsonObject { ["error"] = ex.Message });
                    }
                    return;
                }

                await WriteJsonAsync(ctx.Response, 404, new JsonObject { ["error"] = $"Unknown tool \"{name}\"." });
                return;
            }

            await WriteJsonAsync(ctx.Response, 404, new JsonObject { ["error"] = "Not found." });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grokbot API request failed");
            try
            {
                await WriteJsonAsync(ctx.Response, 500, new JsonObject { ["error"] = "Internal error." });
            }
            catch { /* ignore */ }
        }
    }

    private bool Authorize(HttpListenerRequest request)
    {
        var expected = tokens.Load();
        if (expected is null)
        {
            return false;
        }
        var header = request.Headers["Authorization"];
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var provided = header["Bearer ".Length..].Trim();
        var ba = Encoding.UTF8.GetBytes(provided);
        var bb = Encoding.UTF8.GetBytes(expected);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static JsonObject ProposalJson(MutationProposal p) => new()
    {
        ["id"] = p.Id,
        ["tool"] = p.ToolName,
        ["summary"] = p.Summary,
        ["status"] = p.Status.ToString().ToLowerInvariant(),
        ["created_at"] = p.CreatedAt.ToString("O"),
        ["expires_at"] = p.ExpiresAt.ToString("O"),
        ["result"] = p.ResultJson is { } r ? JsonNode.Parse(r) : null,
        ["error"] = p.Error,
    };

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, JsonNode body)
    {
        await WriteRawAsync(response, status, body.ToJsonString());
    }

    private static async Task WriteRawAsync(HttpListenerResponse response, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    public async ValueTask DisposeAsync() => await StopAsync(persistOff: false);
}
