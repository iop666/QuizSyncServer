using System.Text.Json.Nodes;

namespace QuizSync.Server.Core;

/// <summary>
/// `/info` 的响应构造。
///
/// 两个版本形态**必须分开写**：v1 的字段名与取值是冻结契约（老客户端照着解析），
/// v2 可以增字段但不能改已有语义。见 <c>spec/04-http-api.md</c> 与
/// <c>spec/11-capabilities.md</c>。
/// </summary>
public static class ServerInfo
{
    /// <summary>v1 自述的能力清单（顺序不是契约，客户端只判存在性）。</summary>
    public static readonly string[] V1Capabilities =
        ["analyze", "sync", "image_fetch", "collections", "multipage"];

    public static JsonObject ForV1(ServerOptions options) => new()
    {
        ["device_id"] = options.DeviceId,
        ["device_name"] = options.ServerName,
        ["platform"] = options.Platform,
        ["protocol_version"] = ServerOptions.V1ProtocolVersion,
        ["app_version"] = options.AppVersion,
        ["ai_configured"] = options.AiConfigured,
        ["active_collection_id"] = null,
        ["active_collection_name"] = null,
        ["capabilities"] = new JsonArray([.. V1Capabilities.Select(c => (JsonNode)JsonValue.Create(c)!)]),
    };

    public static JsonObject ForV2(ServerOptions options) => new()
    {
        ["device_id"] = options.DeviceId,
        ["device_name"] = options.ServerName,
        ["platform"] = options.Platform,
        ["protocol_version"] = ServerOptions.ProtocolVersion,
        ["app_version"] = options.AppVersion,
        ["ai_configured"] = options.AiConfigured,
        ["active_collection_id"] = null,
        ["active_collection_name"] = null,
        ["capabilities"] = new JsonArray([.. V1Capabilities.Select(c => (JsonNode)JsonValue.Create(c)!)]),
        // v2 新增：Host 明确自述「同时支持 v1 兼容层多久」，便于客户端规划升级。
        ["v1_compat"] = true,
    };

    public static JsonObject Health(ServerOptions options, TimeSpan uptime) => new()
    {
        ["status"] = "ok",
        ["protocol_version"] = ServerOptions.ProtocolVersion,
        ["app_version"] = options.AppVersion,
        ["uptime_ms"] = (long)uptime.TotalMilliseconds,
    };
}
