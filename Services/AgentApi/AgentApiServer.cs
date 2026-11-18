using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.AgentApi
{
    /// <summary>
    /// Local-only HTTP API (127.0.0.1) so an agent can use GitDeploy like the Telegram bot does.
    /// REST under /v1/*, MCP (JSON-RPC over HTTP) at /mcp, OpenAPI at /v1/openapi.json.
    /// </summary>
    public sealed class AgentApiServer : IDisposable
    {
        public static AgentApiServer Instance { get; } = new();
        public const int DefaultPort = 47821;

        private readonly ConfigurationService _config = new();
        private readonly ConcurrentDictionary<string, (DateTime Window, int Count)> _rate = new();
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private int _port;

        public bool IsRunning => _listener?.IsListening == true;
        public int Port => _port;
        public string LastError { get; private set; } = "";
        public event EventHandler? StateChanged;

        public void StartFromConfig()
        {
            Stop();
            var cfg = _config.LoadGlobalConfig();
            if (!cfg.AgentApiEnabled)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _port = cfg.AgentApiPort is > 1024 and < 65535 ? cfg.AgentApiPort : DefaultPort;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                LastError = "";
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => LoopAsync(_listener, token));
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try
                {
                    _listener?.Close();
                }
                catch
                {
                }

                _listener = null;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Close();
            }
            catch
            {
            }

            _listener = null;
            _cts = null;
        }

        public void Dispose() => Stop();

        private async Task LoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        // ---- request handling --------------------------------------------------------------

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;

                // Browsers always send Origin; agents/CLIs do not. This blocks drive-by web pages and DNS rebinding.
                if (!string.IsNullOrEmpty(req.Headers["Origin"]))
                {
                    await WriteAsync(ctx, 403, Error("Browser requests are not allowed.")).ConfigureAwait(false);
                    return;
                }

                var host = req.Headers["Host"] ?? "";
                if (!host.StartsWith("127.0.0.1:", StringComparison.Ordinal) && !host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(ctx, 403, Error("Bad Host header.")).ConfigureAwait(false);
                    return;
                }

                var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
                if (req.HttpMethod == "GET" && path == "/v1/health")
                {
                    await WriteAsync(ctx, 200, new JObject { ["ok"] = true, ["app"] = "GitDeployPro" }).ConfigureAwait(false);
                    return;
                }

                if (req.HttpMethod == "GET" && path == "/v1/openapi.json")
                {
                    await WriteAsync(ctx, 200, BuildOpenApi()).ConfigureAwait(false);
                    return;
                }

                var key = Authenticate(req);
                if (key == null)
                {
                    ctx.Response.AddHeader("WWW-Authenticate", "Bearer");
                    await WriteAsync(ctx, 401, Error("Missing or invalid API key.")).ConfigureAwait(false);
                    return;
                }

                if (!AllowRate(key.Id))
                {
                    await WriteAsync(ctx, 429, Error("Too many requests (limit 120 per minute).")).ConfigureAwait(false);
                    return;
                }

                if (path == "/mcp")
                {
                    await HandleMcpAsync(ctx, key).ConfigureAwait(false);
                    return;
                }

                var tool = AgentApiTools.Find(req.HttpMethod, path);
                if (tool == null)
                {
                    await WriteAsync(ctx, 404, Error("Unknown endpoint. See /v1/openapi.json.")).ConfigureAwait(false);
                    return;
                }

                JObject args;
                if (req.HttpMethod == "GET")
                {
                    args = new JObject();
                    foreach (var k in req.QueryString.AllKeys.Where(k => k != null))
                    {
                        args[k!] = req.QueryString[k];
                    }
                }
                else
                {
                    var body = await ReadBodyAsync(req).ConfigureAwait(false);
                    try
                    {
                        args = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
                    }
                    catch
                    {
                        await WriteAsync(ctx, 400, Error("Body must be JSON.")).ConfigureAwait(false);
                        return;
                    }
                }

                try
                {
                    var result = await AgentApiActions.ExecuteAsync(tool, args, key).ConfigureAwait(false);
                    var status = tool.Name is "deploy" or "upload" or "terminal_connect" ? 202 : 200;
                    await WriteAsync(ctx, status, result).ConfigureAwait(false);
                }
                catch (AgentApiException ex)
                {
                    await WriteAsync(ctx, ex.Status, Error(ex.Message)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteAsync(ctx, 500, Error(ex.Message)).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private static AgentApiKey? Authenticate(HttpListenerRequest req)
        {
            var header = req.Headers["Authorization"] ?? "";
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : req.Headers["X-Api-Key"];
            return AgentApiKeyStore.Instance.Authenticate(token);
        }

        private bool AllowRate(string keyId)
        {
            var now = DateTime.UtcNow;
            var entry = _rate.AddOrUpdate(
                keyId,
                _ => (now, 1),
                (_, e) => (now - e.Window).TotalSeconds >= 60 ? (now, 1) : (e.Window, e.Count + 1));
            return entry.Count <= 120;
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
        {
            if (req.ContentLength64 > 1_000_000)
            {
                throw new AgentApiException(413, "Body too large.");
            }

            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private static JObject Error(string message) => new() { ["error"] = message };

        private static async Task WriteAsync(HttpListenerContext ctx, int status, JToken body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }

        // ---- MCP (Model Context Protocol, JSON-RPC over HTTP POST) -------------------------

        private static async Task HandleMcpAsync(HttpListenerContext ctx, AgentApiKey key)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                await WriteAsync(ctx, 405, Error("Use POST.")).ConfigureAwait(false);
                return;
            }

            JObject msg;
            try
            {
                msg = JObject.Parse(await ReadBodyAsync(ctx.Request).ConfigureAwait(false));
            }
            catch
            {
                await WriteAsync(ctx, 400, Rpc(null, error: new JObject { ["code"] = -32700, ["message"] = "Parse error" })).ConfigureAwait(false);
                return;
            }

            var id = msg["id"];
            var method = msg["method"]?.ToString() ?? "";
            if (id == null)
            {
                // notification (e.g. notifications/initialized): nothing to answer
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            switch (method)
            {
                case "initialize":
                    await WriteAsync(ctx, 200, Rpc(id, new JObject
                    {
                        ["protocolVersion"] = msg["params"]?["protocolVersion"]?.ToString() ?? "2025-03-26",
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "gitdeploy", ["version"] = typeof(AgentApiServer).Assembly.GetName().Version?.ToString(3) }
                    })).ConfigureAwait(false);
                    return;
                case "ping":
                    await WriteAsync(ctx, 200, Rpc(id, new JObject())).ConfigureAwait(false);
                    return;
                case "tools/list":
                    await WriteAsync(ctx, 200, Rpc(id, new JObject
                    {
                        ["tools"] = new JArray(AgentApiTools.All.Where(t => key.Has(t.Scope)).Select(t => new JObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.InputSchema()
                        }))
                    })).ConfigureAwait(false);
                    return;
                case "tools/call":
                {
                    var name = msg["params"]?["name"]?.ToString() ?? "";
                    var tool = AgentApiTools.FindByName(name);
                    if (tool == null)
                    {
                        await WriteAsync(ctx, 200, Rpc(id, error: new JObject { ["code"] = -32602, ["message"] = "Unknown tool " + name })).ConfigureAwait(false);
                        return;
                    }

                    var args = msg["params"]?["arguments"] as JObject ?? new JObject();
                    try
                    {
                        var result = await AgentApiActions.ExecuteAsync(tool, args, key).ConfigureAwait(false);
                        await WriteAsync(ctx, 200, Rpc(id, ToolResult(result.ToString(Formatting.Indented), false))).ConfigureAwait(false);
                    }
                    catch (AgentApiException ex)
                    {
                        await WriteAsync(ctx, 200, Rpc(id, ToolResult(ex.Message, true))).ConfigureAwait(false);
                    }

                    return;
                }
                default:
                    await WriteAsync(ctx, 200, Rpc(id, error: new JObject { ["code"] = -32601, ["message"] = "Method not found" })).ConfigureAwait(false);
                    return;
            }
        }

        private static JObject ToolResult(string text, bool isError) => new()
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };

        private static JObject Rpc(JToken? id, JObject? result = null, JObject? error = null)
        {
            var o = new JObject { ["jsonrpc"] = "2.0", ["id"] = id };
            if (error != null)
            {
                o["error"] = error;
            }
            else
            {
                o["result"] = result ?? new JObject();
            }

            return o;
        }

        // ---- OpenAPI -----------------------------------------------------------------------

        private JObject BuildOpenApi()
        {
            var paths = new JObject();
            foreach (var t in AgentApiTools.All)
            {
                var op = new JObject
                {
                    ["operationId"] = t.Name,
                    ["summary"] = t.Description,
                    ["description"] = $"Required key scope: {t.Scope}.",
                    ["security"] = new JArray(new JObject { ["bearer"] = new JArray() })
                };

                if (t.Method == "GET")
                {
                    op["parameters"] = new JArray(t.Params.Select(p => new JObject
                    {
                        ["name"] = p.Name,
                        ["in"] = "query",
                        ["required"] = p.Required,
                        ["description"] = p.Description,
                        ["schema"] = new JObject { ["type"] = p.Type == "string[]" ? "string" : p.Type }
                    }));
                }
                else if (t.Params.Length > 0)
                {
                    op["requestBody"] = new JObject
                    {
                        ["required"] = t.Params.Any(p => p.Required),
                        ["content"] = new JObject { ["application/json"] = new JObject { ["schema"] = t.InputSchema() } }
                    };
                }

                op["responses"] = new JObject
                {
                    ["200"] = new JObject { ["description"] = "OK" },
                    ["202"] = new JObject { ["description"] = "Accepted — a job was created; poll /v1/job?id=…" },
                    ["401"] = new JObject { ["description"] = "Missing or invalid key" },
                    ["403"] = new JObject { ["description"] = "Scope or project not allowed" }
                };

                var item = paths[t.Path] as JObject ?? new JObject();
                item[t.Method.ToLowerInvariant()] = op;
                paths[t.Path] = item;
            }

            return new JObject
            {
                ["openapi"] = "3.0.3",
                ["info"] = new JObject
                {
                    ["title"] = "GitDeploy Pro Agent API",
                    ["version"] = typeof(AgentApiServer).Assembly.GetName().Version?.ToString(3),
                    ["description"] = "Local API (127.0.0.1) for agents. Auth: Authorization: Bearer <key>. MCP endpoint: POST /mcp."
                },
                ["servers"] = new JArray(new JObject { ["url"] = $"http://127.0.0.1:{_port}" }),
                ["components"] = new JObject
                {
                    ["securitySchemes"] = new JObject
                    {
                        ["bearer"] = new JObject { ["type"] = "http", ["scheme"] = "bearer" }
                    }
                },
                ["paths"] = paths
            };
        }
    }
}
