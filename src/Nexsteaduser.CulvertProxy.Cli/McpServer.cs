using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexsteaduser.CulvertProxy.Core.Control;

namespace Nexsteaduser.CulvertProxy.Cli;

/// <summary>通过 stdio 暴露 MCP 工具，控制正在运行的 CulvertProxy 界面进程。</summary>
internal static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private const int PipeConnectTimeoutMs = 2000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? response;
            try
            {
                response = await HandleAsync(line, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = Error(null, -32603, ex.Message);
            }

            if (response is not null)
            {
                await output.WriteLineAsync(response.ToJsonString(JsonOptions)).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task<JsonObject?> HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject
                ?? throw new JsonException("请求必须是 JSON 对象。");
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, ex.Message);
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (string.IsNullOrEmpty(method))
        {
            return Error(id, -32600, "缺少 method。");
        }

        if (method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return null;
        }

        switch (method)
        {
            case "initialize":
                return Result(id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new
                    {
                        tools = new { listChanged = false },
                    },
                    serverInfo = new
                    {
                        name = "culvertproxy",
                        title = "CulvertProxy",
                        version = "1.0.3",
                    },
                });
            case "ping":
                return Result(id, new { });
            case "tools/list":
                return Result(id, new { tools = ToolDefinitions() });
            case "tools/call":
                return await CallToolAsync(id, request["params"] as JsonObject, cancellationToken).ConfigureAwait(false);
            case "resources/list":
                return Result(id, new { resources = Array.Empty<object>() });
            case "prompts/list":
                return Result(id, new { prompts = Array.Empty<object>() });
            case "logging/setLevel":
                return Result(id, new { });
            default:
                return Error(id, -32601, $"不支持的方法：{method}。");
        }
    }

    private static async Task<JsonObject> CallToolAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
        {
            return Error(id, -32602, "缺少工具名。");
        }

        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        var (operation, args) = MapTool(name, arguments);
        if (operation is null)
        {
            return Error(id, -32602, $"未知工具：{name}。");
        }

        try
        {
            var data = await SendAsync(operation, args, cancellationToken).ConfigureAwait(false);
            return Result(id, new
            {
                content = new[]
                {
                    new { type = "text", text = data?.ToJsonString(PrettyJson) ?? "{}" },
                },
                isError = false,
            });
        }
        catch (Exception ex)
        {
            return Result(id, new
            {
                content = new[]
                {
                    new { type = "text", text = ex.Message },
                },
                isError = true,
            });
        }
    }

    private static (string? Operation, JsonObject Arguments) MapTool(string name, JsonObject arguments)
    {
        return name switch
        {
            "culvert_status" => ("status", new JsonObject()),
            "culvert_get_config" => ("config", new JsonObject()),
            "culvert_list_connections" => ("connections", new JsonObject
            {
                ["activeOnly"] = arguments["active_only"]?.GetValue<bool>() ?? arguments["activeOnly"]?.GetValue<bool>() ?? false,
                ["limit"] = arguments["limit"]?.GetValue<int>() ?? 200,
            }),
            "culvert_list_endpoints" => ("endpoints", new JsonObject()),
            "culvert_list_rules" => ("rules", new JsonObject()),
            "culvert_list_policies" => ("policies", new JsonObject()),
            "culvert_start" => ("start", new JsonObject()),
            "culvert_stop" => ("stop", new JsonObject()),
            "culvert_reload" => ("reload", new JsonObject()),
            "culvert_test_endpoint" => ("test_endpoint", new JsonObject
            {
                ["endpointId"] = arguments["endpoint_id"]?.GetValue<string>() ?? arguments["endpointId"]?.GetValue<string>(),
                ["host"] = arguments["host"]?.GetValue<string>(),
                ["port"] = arguments["port"]?.GetValue<int>(),
            }),
            "culvert_kill_connection" => ("kill_connection", new JsonObject
            {
                ["id"] = arguments["id"]?.GetValue<long>(),
            }),
            _ => (null, new JsonObject()),
        };
    }

    private static async Task<JsonNode?> SendAsync(string operation, JsonObject arguments, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            ControlProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(PipeConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            throw new IOException("CulvertProxy 界面进程未运行，或 MCP 控制管道不可用。先启动 CulvertProxy.exe 再重试。", ex);
        }

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var request = new JsonObject
        {
            ["op"] = operation,
            ["args"] = arguments,
        };
        await writer.WriteLineAsync(request.ToJsonString(JsonOptions)).ConfigureAwait(false);

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("控制管道提前关闭。");
        var response = JsonNode.Parse(line) as JsonObject
            ?? throw new IOException("控制管道响应格式无效。");
        if (response["ok"]?.GetValue<bool>() != true)
        {
            throw new IOException(response["error"]?.GetValue<string>() ?? "控制操作失败。");
        }

        return response["data"]?.DeepClone();
    }

    private static JsonArray ToolDefinitions()
    {
        return
        [
            Tool("culvert_status", "读取代理引擎状态、监听端口、吞吐、活动连接和透明接管状态。", new JsonObject()),
            Tool("culvert_get_config", "读取脱敏配置：入口、DNS、引擎、接管、服务器、策略与规则摘要。密码不会返回。", new JsonObject()),
            Tool("culvert_list_connections", "列出活动连接与最近历史，包含进程、命中规则、链路、状态和字节数。", new JsonObject
            {
                ["activeOnly"] = BooleanProperty("只返回活动连接，默认 false。"),
                ["limit"] = IntegerProperty("最多返回条数，1 到 500，默认 200。", 1, 500),
            }),
            Tool("culvert_list_endpoints", "列出已配置上游服务器，密码字段始终脱敏。", new JsonObject()),
            Tool("culvert_list_rules", "列出路由规则及其动作与策略引用。", new JsonObject()),
            Tool("culvert_list_policies", "列出策略、选择模式与链路。", new JsonObject()),
            Tool("culvert_start", "启动本地代理引擎与透明接管（如果已配置）。", new JsonObject()),
            Tool("culvert_stop", "停止本地代理引擎，并断开活动连接。", new JsonObject()),
            Tool("culvert_reload", "从磁盘重新加载 config.json，并热更新引擎。", new JsonObject()),
            Tool("culvert_test_endpoint", "对指定上游测试到目标主机的握手延迟。", new JsonObject
            {
                ["endpoint_id"] = StringProperty("服务器 Id。"),
                ["host"] = StringProperty("测试目标主机。"),
                ["port"] = IntegerProperty("测试目标端口。", 1, 65535),
            }, "endpoint_id", "host", "port"),
            Tool("culvert_kill_connection", "按连接 Id 断开一条活动连接。", new JsonObject
            {
                ["id"] = IntegerProperty("连接 Id，可从 culvert_list_connections 获取。", 1, long.MaxValue),
            }, "id"),
        ];
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(value => (JsonNode)value!).ToArray());
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static JsonObject StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static JsonObject BooleanProperty(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description,
    };

    private static JsonObject IntegerProperty(string description, long minimum, long maximum) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["minimum"] = minimum,
        ["maximum"] = maximum,
    };

    private static JsonObject Result(JsonNode? id, object result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = JsonSerializer.SerializeToNode(result, JsonOptions),
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
    }
}

