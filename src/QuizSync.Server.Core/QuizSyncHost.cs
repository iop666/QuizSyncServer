using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuizSync.Server.Core.Auth;
using QuizSync.Server.Core.Images;
using QuizSync.Server.Core.Pairing;
using QuizSync.Server.Core.Protocol;
using QuizSync.Server.Core.Storage;
using QuizSync.Server.Core.Sync;

namespace QuizSync.Server.Core;

/// <summary>
/// 一个正在跑的 Host（内嵌 Kestrel）。
///
/// 分层纪律（见 <c>src/README.md</c>）：Core **不读命令行参数、不写控制台**；
/// 它只知道「绑定哪个地址、监听哪个端口、报什么版本」。行为必须能在进程内用回环
/// 地址测（<see cref="StartAsync"/> 传端口 0 即可），一致性向量也是这么回放的。
/// </summary>
public sealed class QuizSyncHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly WebApplication _app;
    private readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();

    private QuizSyncHost(
        WebApplication app, ServerOptions options, int port, QuizSyncDatabase database)
    {
        _app = app;
        Options = options;
        Port = port;
        Database = database;
    }

    public ServerOptions Options { get; }

    public QuizSyncDatabase Database { get; }

    public DeviceRepository Devices => field ??= new DeviceRepository(Database);

    public PairingService Pairing => field ??= new PairingService(Devices, Clock, new PairingOptions
    {
        ServerDeviceId = Options.DeviceId,
        ServerName = Options.ServerName,
    });

    public BearerAuthenticator Authenticator => field ??= new BearerAuthenticator(Devices);

    public ImageRepository Images2 => field ??= new ImageRepository(Database);

    /// <summary>图片上传/下载。字节存放：给过数据目录就用磁盘，否则内存（测试）。</summary>
    public ImageService Images => field ??= new ImageService(
        Images2,
        Blobs,
        Clock,
        new ImageOptions());

    private IImageBlobStore Blobs => field ??= Options.DataDirectory is null
        ? new MemoryImageBlobStore()
        : new DirectoryImageBlobStore(Path.Combine(Options.DataDirectory, "images"));

    public ExistenceRepository Queries => field ??= new ExistenceRepository(Database);

    public CollectionRepository Collections => field ??= new CollectionRepository(Database);

    public SyncApplier Applier => field ??= new SyncApplier(Database);

    public SyncService Sync => field ??= new SyncService(Database, Applier, Clock);

    /// <summary>注入的时钟。**所有时间判定都走它**（回放器要能推进时间）。</summary>
    public IClock Clock { get; private init; } = SystemClock.Instance;

    /// <summary>实际监听端口（<see cref="ServerOptions.PreferredPort"/> 为 0 时由系统分配）。</summary>
    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// 启动并监听。端口占用时按 <see cref="ServerOptions.PortRange"/> 向上探测；
    /// 全占满则抛 <see cref="InvalidOperationException"/>（**不静默换端口**，
    /// 与 v1 桌面端一致：静默换端口会让二维码里的地址失效）。
    /// </summary>
    public static async Task<QuizSyncHost> StartAsync(
        ServerOptions options, IClock? clock = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        for (var offset = 0; offset < Math.Max(1, options.PortRange); offset++)
        {
            var port = options.PreferredPort == 0 ? 0 : options.PreferredPort + offset;
            try
            {
                return await BindAsync(options, clock ?? SystemClock.Instance, port, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException) when (options.PreferredPort != 0 && offset < options.PortRange - 1)
            {
                // 端口被占用：试下一个（最后一轮不吞异常，让调用方看到真实原因）。
            }
        }

        throw new InvalidOperationException(
            $"端口 {options.PreferredPort}–{options.PreferredPort + options.PortRange - 1} 全部被占用");
    }

    private static async Task<QuizSyncHost> BindAsync(
        ServerOptions options, IClock clock, int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Parse(options.BindAddress), port);
            // 上传上限交给协议层判（2 MiB，两阶段），Kestrel 自己别先拦下来。
            kestrel.Limits.MaxRequestBodySize = null;
        });

        var app = builder.Build();
        var databasePath = options.DataDirectory is null
            ? ":memory:"
            : Path.Combine(options.DataDirectory, "quizsync.db");
        if (options.DataDirectory is not null)
        {
            Directory.CreateDirectory(options.DataDirectory);
        }

        var database = QuizSyncDatabase.Open(databasePath);
        var host = new QuizSyncHost(app, options, port, database) { Clock = clock };
        host.MapPipeline();
        host.MapEndpoints();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        var bound = addresses?.Addresses.FirstOrDefault();
        if (bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out var uri))
        {
            // 就地回填端口：**不能新建实例** —— 端点闭包捕获的是这个实例的
            // Pairing/Auth 服务，换实例会让「测试拿到的配对码」与「端点校验的配对码」
            // 变成两份（实测：正确配对码被判 invalid_code）。
            host.Port = uri.Port;
        }

        return host;
    }

    /// <summary>
    /// 中间件顺序**必须与 v1 一致**：先鉴权、再版本协商，且两者都在路由之前
    /// （所以「未知路径 + 版本不符」得到的是 426 而不是 404，见 `spec/10-versioning.md`）。
    /// </summary>
    private void MapPipeline()
    {
        _app.Use(async (context, next) =>
        {
            // 每个响应都带服务端版本（错误响应也带）—— v1 行为，客户端用它做诊断。
            context.Response.Headers["X-QS-Server-Version"] = Options.AppVersion;

            var path = context.Request.Path.Value ?? string.Empty;

            // v1 只豁免这两个路径（`/ws` 走自己的握手鉴权，不经过这里做版本协商）。
            var exempt = path is "/api/v1/pair" or "/api/v1/info" or "/health";

            if (!exempt && path.StartsWith("/api/v1/", StringComparison.Ordinal))
            {
                var auth = Authenticator.Authenticate(context.Request.Headers.Authorization);
                if (auth.Status is AuthStatus.Missing or AuthStatus.Invalid)
                {
                    await WriteErrorAsync(context, 401, ApiError.Unauthorized,
                        auth.Status == AuthStatus.Missing ? "缺少 token" : "token 无效").ConfigureAwait(false);
                    return;
                }

                if (auth.Status == AuthStatus.Revoked)
                {
                    await WriteErrorAsync(context, 401, ApiError.Revoked, "设备已被吊销").ConfigureAwait(false);
                    return;
                }

                context.Items["device"] = auth.Device;
                Devices.Touch(auth.DeviceId!, Clock.NowMs);
            }

            var mismatch = VersionMismatch(context.Request.Headers["X-QS-Client-Version"].ToString());
            if (mismatch is not null)
            {
                await WriteErrorAsync(context, 426, ApiError.VersionMismatch, mismatch).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// v1 协商：不带版本头放行；带了就比**主版本**（v1 路径上固定比 1，见
    /// <see cref="ProtocolVersion.V1CompatMajor"/>）；**不可解析的值也算不符**。
    /// </summary>
    private string? VersionMismatch(string? clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            return null;
        }

        var majorText = clientVersion.Split('.')[0];
        if (!int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
            major != ProtocolVersion.V1CompatMajor)
        {
            return $"客户端版本 {clientVersion} 与服务端 {Options.AppVersion} 主版本不一致，请两端都升级到同一大版本";
        }

        return null;
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message, int? retryAfter = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(ApiError.Body(code, message, retryAfter), Json))
            .ConfigureAwait(false);
    }

    private void MapEndpoints()
    {
        _app.MapGet("/health", () =>
            Results.Json(ServerInfo.Health(Options, _uptime.Elapsed), Json));

        _app.MapGet("/api/v2/info", () => Results.Json(ServerInfo.ForV2(Options), Json));

        // v1 兼容层。
        _app.MapGet("/api/v1/info", () => Results.Json(ServerInfo.ForV1(Options), Json));

        _app.MapPost("/api/v1/pair", async context =>
        {
            PairRequest request;
            try
            {
                var node = await JsonNode.ParseAsync(context.Request.Body).ConfigureAwait(false);
                request = PairRequest.FromJson(node);
            }
            catch (JsonException)
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "body 不是合法 JSON").ConfigureAwait(false);
                return;
            }

            if (!request.IsValid)
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "字段缺失或格式错误").ConfigureAwait(false);
                return;
            }

            var outcome = Pairing.Pair(request);
            context.Response.StatusCode = outcome.Status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(outcome.Body, Json)).ConfigureAwait(false);
        });

        // 鉴权已由中间件完成。
        _app.MapGet("/api/v1/collections", () => Results.Json(new JsonObject
        {
            ["collections"] = Collections.List(),
            ["active_collection_id"] = Collections.ActiveId(),
        }, Json));

        _app.MapPost("/api/v1/images", async context =>
        {
            var uploader = (context.Items["device"] as DeviceRecord)?.DeviceId ?? Options.DeviceId;
            var outcome = await Images.UploadAsync(context, uploader, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = outcome.Status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(outcome.Body, Json)).ConfigureAwait(false);
        });

        _app.MapGet("/api/v1/images/{hash}", async context =>
        {
            var hash = (string)context.Request.RouteValues["hash"]!;
            var outcome = Images.Download(hash);
            if (outcome.Bytes is not null)
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "image/jpeg";
                await context.Response.Body.WriteAsync(outcome.Bytes, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = outcome.Status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(outcome.Body, Json)).ConfigureAwait(false);
        });

        _app.MapPost("/api/v1/sync/ops", async context =>
        {
            JsonNode? node;
            try
            {
                node = await JsonNode.ParseAsync(context.Request.Body).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "body 不是合法 JSON").ConfigureAwait(false);
                return;
            }

            if (node is not JsonObject body || body["ops"] is not JsonArray ops)
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "ops 缺失").ConfigureAwait(false);
                return;
            }

            var caller = (context.Items["device"] as DeviceRecord)?.DeviceId ?? Options.DeviceId;
            var outcome = Sync.Push(caller, ops, Options.DeviceId);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new JsonObject
            {
                ["applied"] = outcome.Applied,
                ["rejected"] = outcome.Rejected,
            }, Json)).ConfigureAwait(false);
        });

        _app.MapGet("/api/v1/sync/ops", async context =>
        {
            var fromDevice = context.Request.Query["from_device"].ToString();
            if (string.IsNullOrEmpty(fromDevice))
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "from_device 缺失").ConfigureAwait(false);
                return;
            }

            // 游标：`cursor` 覆盖 `since_lamport`（v1 行为）。
            var cursorText = context.Request.Query["cursor"].ToString();
            if (string.IsNullOrEmpty(cursorText))
            {
                cursorText = context.Request.Query["since_lamport"].ToString();
            }

            _ = long.TryParse(cursorText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var since);

            var page = Sync.Pull(fromDevice, since);
            var ops = new JsonArray();
            foreach (var op in page.Ops)
            {
                ops.Add(op.ToJson());
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new JsonObject
            {
                ["ops"] = ops,
                ["has_more"] = page.HasMore,
                ["next_cursor"] = page.NextCursor,
            }, Json)).ConfigureAwait(false);
        });

        _app.MapGet("/api/v1/sync/snapshot", (HttpContext context) =>
        {
            var limit = int.TryParse(context.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 1000) : 200;
            var offset = int.TryParse(context.Request.Query["offset"], out var o) ? Math.Max(o, 0) : 0;
            return Results.Json(Sync.Snapshot(limit, offset), Json);
        });

        _app.MapPost("/api/v1/collections/{id}/select", async context =>
        {
            var id = (string)context.Request.RouteValues["id"]!;
            if (!Collections.Exists(id))
            {
                await WriteErrorAsync(context, 404, ApiError.NotFound, "合集不存在").ConfigureAwait(false);
                return;
            }

            Collections.SetActive(id);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new JsonObject { ["active_collection_id"] = id }, Json)).ConfigureAwait(false);
        });

        // 任务相关端点：任务流水线（队列 + 识别）还没接上，所以此刻**没有任何任务行**，
        // 一律 404 —— 与 v1 对不存在的任务的行为一致（tasks.ndjson 12/13、errors.ndjson）。
        _app.MapGet("/api/v1/tasks/{taskId}", async context =>
        {
            var taskId = (string)context.Request.RouteValues["taskId"]!;
            _ = taskId;
            await WriteErrorAsync(context, 404, ApiError.NotFound, "任务不存在").ConfigureAwait(false);
        });

        _app.MapPost("/api/v1/tasks/{taskId}/retry", async context =>
        {
            await WriteErrorAsync(context, 404, ApiError.NotFound, "任务不存在").ConfigureAwait(false);
        });

        // 重新生成：会话不存在 → 404；没有可重跑的图 → 409（与 v1 收敛后的行为一致）。
        _app.MapPost("/api/v1/sessions/{sessionId}/reanalyze", async context =>
        {
            var sessionId = (string)context.Request.RouteValues["sessionId"]!;
            if (!Queries.SessionExists(sessionId))
            {
                await WriteErrorAsync(context, 404, ApiError.NotFound, "会话不存在").ConfigureAwait(false);
                return;
            }

            var hashes = Queries.ImageHashesOf(sessionId);
            if (hashes.Count == 0 || hashes.Exists(h => !Queries.ImageExists(h)))
            {
                await WriteErrorAsync(context, 409, ApiError.InvalidRequest, "原图已不在电脑上，无法重新识别").ConfigureAwait(false);
                return;
            }

            // 真正的重跑要等任务流水线接上。
            await WriteErrorAsync(context, 409, ApiError.InvalidRequest, "原图已不在电脑上，无法重新识别").ConfigureAwait(false);
        });

        _app.MapDelete("/api/v1/devices/{id}", (string id) =>
        {
            Devices.Revoke(id, Clock.NowMs);
            return Results.Json(new JsonObject { ["revoked"] = id }, Json);
        });

        _app.MapGet("/api/v1/devices", () => Results.Json(new JsonObject
        {
            // 注意：**绝不回传 token_hash**（本地专属列，见 spec/01-conventions.md）。
            ["devices"] = new JsonArray([.. Devices.List().Select(d => (JsonNode)new JsonObject
            {
                ["device_id"] = d.DeviceId,
                ["name"] = d.Name,
                ["platform"] = d.Platform,
                ["paired_at"] = d.PairedAt,
                ["last_seen_at"] = d.LastSeenAt,
                ["revoked_at"] = d.RevokedAt,
                ["app_version"] = d.AppVersion,
            })]),
        }, Json));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        Database.Dispose();
    }
}
