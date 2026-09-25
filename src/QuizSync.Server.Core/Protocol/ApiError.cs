using System.Text.Json.Nodes;

namespace QuizSync.Server.Core.Protocol;

/// <summary>
/// 统一的错误体：`{code, message, retry_after_seconds}`。
///
/// **`retry_after_seconds` 键恒在**（不适用时为 `null`），且**不用 `Retry-After` 头**
/// —— 这是 v1 冻结的形态（见 <c>spec/09-errors.md</c>）。客户端分支只看 `code`。
/// </summary>
public static class ApiError
{
    public const string Unauthorized = "unauthorized";
    public const string Revoked = "revoked";
    public const string VersionMismatch = "version_mismatch";
    public const string InvalidCode = "invalid_code";
    public const string CodeExpired = "code_expired";
    public const string RateLimited = "rate_limited";
    public const string InvalidRequest = "invalid_request";
    public const string TooManyPages = "too_many_pages";
    public const string NoActiveCollection = "no_active_collection";
    public const string QueueFull = "queue_full";
    public const string PayloadTooLarge = "payload_too_large";
    public const string NotFound = "not_found";
    public const string Internal = "internal";

    public static JsonObject Body(string code, string message, int? retryAfterSeconds = null) => new()
    {
        ["code"] = code,
        ["message"] = message,
        ["retry_after_seconds"] = retryAfterSeconds,
    };
}
