using System.Globalization;
using System.Net;
using System.Net.WebSockets;
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
using QuizSync.Server.Core.Tasks;
using QuizSync.Server.Core.Ws;

// 协议里的任务状态叫 TaskStatus，与 System.Threading.Tasks.TaskStatus 撞名 —— 起个别名。
using TaskStatus = QuizSync.Server.Core.Tasks.TaskStatus;

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

    public TaskRepository TaskRows => field ??= new TaskRepository(Database);

    public SessionRepository Sessions => field ??= new SessionRepository(Database);

    public TaskService Tasks => field ??= new TaskService(
        TaskRows, Sessions, Images2, Blobs, Collections, Analyzer, Clock, new TaskOptions());

    /// <summary>
    /// 识别器：默认是固定答案的 fixture（回放要**确定性**，不要真 AI）。
    /// 真机上以后换成「派发给 Windows 客户端的 Provider」。
    /// </summary>
    public IAnalyzer Analyzer { get; init; } = new FixtureAnalyzer();

    public WebSocketHub Hub => field ??= new WebSocketHub();

    /// <summary>
    /// 桌面端切合集后走的就是这里（真实产品入口）：立刻广播 `collection_changed`，
    /// 客户端不必等下一次轮询。
    /// </summary>
    public void NotifyCollectionChanged()
    {
        Hub.Broadcast(new JsonObject
        {
            ["type"] = "collection_changed",
            ["collection_id"] = Collections.ActiveId(),
            ["collection_name"] = ActiveCollectionName(),
        });
    }

    /// <summary>当前选中合集的名字（没有选中时 null）。</summary>
    private string? ActiveCollectionName()
    {
        var active = Collections.ActiveId();
        if (active is null)
        {
            return null;
        }

        foreach (var row in Collections.List())
        {
            if (row?["collection_id"]?.ToString() == active)
            {
                return row["name"]?.ToString();
            }
        }

        return null;
    }

    /// <summary>注入的时钟。**所有时间判定都走它**（回放器要能推进时间）。</summary>
    public IClock Clock { get; private init; } = SystemClock.Instance;

    /// <summary>实际监听端口（<see cref="ServerOptions.PreferredPort"/> 为 0 时由系统分配）。</summary>
    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// 已注册的**路由模板**（供「每条路由都要有负向鉴权用例」这类机械检查用）。
    ///
    /// 加这条的由来：v2 的路由加进来时鉴权中间件只判 `/api/v1/` 前缀，
    /// 于是 `/api/v2/*` 整段裸奔。人眼盯不住，让测试按路由表逐条打。
    /// </summary>
    public IReadOnlyList<string> ApiRoutePatterns => _app.Services
        .GetRequiredService<EndpointDataSource>()
        .Endpoints
        .OfType<RouteEndpoint>()
        .Select(e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
        .Where(p => p.StartsWith("/api/", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList();

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

        // 响应压缩（v2 清单之一）：同步快照与分页在大库上会到几百 KB，
        // 局域网里也值得压。**只压 JSON 且超过阈值**（小响应压了反而更慢）。
        builder.Services.AddResponseCompression(compression =>
        {
            compression.EnableForHttps = false; // 局域网是明文 HTTP；开 HTTPS 压缩只会招来 BREACH 那类问题
            compression.MimeTypes = ["application/json", "application/ndjson"];
        });
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
        _app.UseResponseCompression();
        _app.UseWebSockets();

        // 任务状态 → WS 广播（唯一出口，与 v1 的 `_emitTaskUpdate` 对齐）。
        Tasks.OnTaskUpdate = (taskId, status, sessionId, imageCount) =>
        {
            var count = imageCount;
            if (count <= 0 && !string.IsNullOrEmpty(sessionId))
            {
                count = Sessions.PageHashes(sessionId).Count;
            }

            var update = new JsonObject
            {
                ["type"] = "task_update",
                ["task_id"] = taskId,
                ["status"] = status,
                // v1 里 session_id 为 null 时**键被省略**。
                ["image_count"] = count,
            };
            if (sessionId is not null)
            {
                update["session_id"] = sessionId;
            }

            Hub.Broadcast(update);

            if (status == TaskStatus.Done && sessionId is not null)
            {
                var session = Sessions.SessionJson(sessionId);
                if (session?["deleted_at"] is null && session is not null)
                {
                    Hub.Broadcast(new JsonObject
                    {
                        ["type"] = "task_result",
                        ["task_id"] = taskId,
                        ["session"] = session,
                    });
                }
            }
            else if (status == TaskStatus.Failed && sessionId is not null)
            {
                var session = Sessions.SessionJson(sessionId);
                Hub.Broadcast(new JsonObject
                {
                    ["type"] = "task_failed",
                    ["task_id"] = taskId,
                    ["error_code"] = session?["error_code"]?.ToString() ?? ApiError.Internal,
                    ["message"] = session?["error_message"]?.ToString() ?? "分析失败",
                });
            }
        };

        _app.Use(async (context, next) =>
        {
            // 每个响应都带服务端版本（错误响应也带）—— v1 行为，客户端用它做诊断。
            context.Response.Headers["X-QS-Server-Version"] = Options.AppVersion;

            var path = context.Request.Path.Value ?? string.Empty;

            // v1 只豁免这几个路径（`/ws` 走自己的握手鉴权，不经过这里做版本协商）。
            // 本机控制面（`/api/v1/pair/code*`）也不是 LAN 协议：它由**控制令牌**把关，
            // 不要求设备 Bearer（否则 CLI 得先配对才能读配对码，死循环）。
            // `/api/v2/info` 与 v1 的 `/info` 同级，同样免鉴权（客户端靠它做版本协商）。
            var exempt = path is "/api/v1/pair" or "/api/v1/info" or "/api/v2/info" or "/health"
                or "/api/v1/pair/code" or "/api/v1/pair/code/refresh";

            // ⚠️ 鉴权必须覆盖**所有** API 版本：原来只判 `/api/v1/` 前缀，
            // 于是 `/api/v2/*` 整段绕过鉴权（未配对的局域网设备能直接拉同步 op）——
            // v2 的拉取用例第一次跑就把这条抓出来了。
            var isApi = path.StartsWith("/api/v1/", StringComparison.Ordinal)
                || path.StartsWith("/api/v2/", StringComparison.Ordinal);

            if (!exempt && isApi)
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

    /// <summary>
    /// 本机控制面的准入：**必须来自回环地址**，且控制令牌对得上（`X-QS-Control` 头）。
    /// 不满足就回 403 `forbidden`（与独立服务端 v1 同码）。未配置控制令牌 = 不开放。
    /// </summary>
    private async Task<bool> RequireControlAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        var loopback = remote is not null && System.Net.IPAddress.IsLoopback(remote);
        if (!loopback)
        {
            await WriteErrorAsync(context, 403, "forbidden", "只允许本机调用").ConfigureAwait(false);
            return false;
        }

        if (string.IsNullOrEmpty(Options.ControlToken))
        {
            await WriteErrorAsync(context, 501, "unavailable", "这个实例没有开放本机控制面").ConfigureAwait(false);
            return false;
        }

        if (!string.Equals(context.Request.Headers["X-QS-Control"].ToString(), Options.ControlToken, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context, 403, "forbidden", "缺少或错误的控制令牌").ConfigureAwait(false);
            return false;
        }

        return true;
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

        // ---- 本机控制面（**不属于 LAN 协议**）----
        // 只在回环地址 + 控制令牌都对时才服务；否则一律 403 forbidden
        // （那两个码在 spec/09-errors.md §7.1 里被划到「本机控制面」）。
        _app.MapGet("/api/v1/pair/code", async context =>
        {
            if (!await RequireControlAsync(context).ConfigureAwait(false))
            {
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new JsonObject
            {
                ["code"] = Pairing.Code,
                ["expires_at"] = Pairing.ExpiresAtMs,
                ["device_id"] = Options.DeviceId,
                ["device_name"] = Options.ServerName,
                ["port"] = Port,
                // 客户端扫这个深链就能配对（二维码里放的就是它）。
                ["pair_uri"] = $"quizsync://pair?host=127.0.0.1&port={Port}&code={Pairing.Code}",
            }, Json)).ConfigureAwait(false);
        });

        _app.MapPost("/api/v1/pair/code/refresh", async context =>
        {
            if (!await RequireControlAsync(context).ConfigureAwait(false))
            {
                return;
            }

            var code = Pairing.RefreshCode();
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new JsonObject
            {
                ["code"] = code,
                ["expires_at"] = Pairing.ExpiresAtMs,
            }, Json)).ConfigureAwait(false);
        });

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

            // 落库失败要变成**协议错误**而不是 500：op 里少了「NOT NULL 且无默认值」的列
            // （例如 questions 的 session_id / ordinal / type）时，SQLite 会抛约束异常。
            // 客户端拿到 400 + 原因才能自救；给个 500 只能两眼一抹黑。
            PushOutcome outcome;
            try
            {
                outcome = Sync.Push(caller, ops, Options.DeviceId);
            }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, $"op 无法落地：{error.Message}").ConfigureAwait(false);
                return;
            }

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

        // v2：`since_lamport` 与 `cursor` **收敛为单一游标参数**（见 spec/04 第 6 条）。
        // 老参数不再静默忽略 —— 明确报错，否则客户端会以为自己在用 v1 语义。
        _app.MapGet("/api/v2/sync/ops", async context =>
        {
            if (!string.IsNullOrEmpty(context.Request.Query["since_lamport"].ToString()))
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest,
                    "v2 用 cursor；since_lamport 已废弃").ConfigureAwait(false);
                return;
            }

            var fromDevice = context.Request.Query["from_device"].ToString();
            if (string.IsNullOrEmpty(fromDevice))
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "from_device 缺失").ConfigureAwait(false);
                return;
            }

            _ = long.TryParse(context.Request.Query["cursor"].ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var cursor);

            var page = Sync.Pull(fromDevice, cursor);
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

        // 任务流水线：校验顺序、幂等、结果复用、队列深度、202 恒定语义。
        _app.MapPost("/api/v1/tasks", async context =>
        {
            JsonObject body;
            try
            {
                var node = await JsonNode.ParseAsync(context.Request.Body).ConfigureAwait(false);
                if (node is not JsonObject obj)
                {
                    await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "body 不是合法 JSON").ConfigureAwait(false);
                    return;
                }

                body = obj;
            }
            catch (JsonException)
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "body 不是合法 JSON").ConfigureAwait(false);
                return;
            }

            var caller = (context.Items["device"] as DeviceRecord)?.DeviceId ?? Options.DeviceId;
            var outcome = Tasks.Submit(body, caller, Options.DeviceId);
            context.Response.StatusCode = outcome.Status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(outcome.Body, Json)).ConfigureAwait(false);
        });

        _app.MapGet("/api/v1/tasks/active", () => Results.Json(new JsonObject
        {
            ["status"] = "idle",
            ["task_id"] = null,
            ["session_id"] = null,
            ["image_count"] = 0,
            ["updated_at"] = Clock.NowMs,
            ["message"] = null,
            ["active_collection_id"] = Collections.ActiveId(),
            ["collections"] = Collections.List(),
            ["ops_lamport"] = Sync.Watermark(),
        }, Json));

        _app.MapGet("/api/v1/tasks/{taskId}", async context =>
        {
            var taskId = (string)context.Request.RouteValues["taskId"]!;
            var view = Tasks.TaskView(taskId);
            if (view is null)
            {
                await WriteErrorAsync(context, 404, ApiError.NotFound, "任务不存在").ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(view, Json)).ConfigureAwait(false);
        });

        _app.MapPost("/api/v1/tasks/{taskId}/retry", async context =>
        {
            var taskId = (string)context.Request.RouteValues["taskId"]!;
            if (!Tasks.Retry(taskId))
            {
                await WriteErrorAsync(context, 404, ApiError.NotFound, "任务不存在").ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new JsonObject { ["status"] = "queued" }, Json)).ConfigureAwait(false);
        });

        // 重新生成：会话不存在 → 404；没有可重跑的图 → 409；否则 202 + 新 task_id。
        _app.MapPost("/api/v1/sessions/{sessionId}/reanalyze", async context =>
        {
            var sessionId = (string)context.Request.RouteValues["sessionId"]!;
            var caller = (context.Items["device"] as DeviceRecord)?.DeviceId ?? Options.DeviceId;
            var outcome = Tasks.Reanalyze(sessionId, caller);
            if (outcome is null)
            {
                await WriteErrorAsync(context, 404, ApiError.NotFound, "会话不存在").ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = outcome.Status;
            context.Response.ContentType = "application/json; charset=utf-8";
            var body = outcome.Body;
            // 重新生成的响应必须带新任务号（客户端靠它轮询）。
            if (outcome.Status == 202 && body["task_id"] is null)
            {
                body["task_id"] = null;
            }

            await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json)).ConfigureAwait(false);
        });

        _app.MapDelete("/api/v1/devices/{id}", async (string id) =>
        {
            Devices.Revoke(id, Clock.NowMs);
            // 先推 device_revoked，再关连接（顺序有讲究：反过来的话客户端收不到事件）。
            await Hub.BroadcastAsync(new JsonObject
            {
                ["type"] = "device_revoked",
                ["device_id"] = id,
            }).ConfigureAwait(false);
            await Hub.CloseForAsync(id).ConfigureAwait(false);
            return Results.Json(new JsonObject { ["revoked"] = id }, Json);
        });

        // `/ws`：同一个端口上的事件推送。握手鉴权只看 Authorization 头，
        // 且**不参与**版本协商（与 v1 一致）。
        _app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await WriteErrorAsync(context, 400, ApiError.InvalidRequest, "需要 WebSocket 升级").ConfigureAwait(false);
                return;
            }

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

            var deviceId = auth.DeviceId!;
            var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await Hub.AddAsync(deviceId, socket).ConfigureAwait(false);
            Devices.Touch(deviceId, Clock.NowMs);

            // hello 自述：协议版本按**兼容层**报 1（老客户端只认 1）。
            var activeId = Collections.ActiveId();
            Hub.Send(deviceId, new JsonObject
            {
                ["type"] = "hello",
                ["server_device_id"] = Options.DeviceId,
                ["protocol_version"] = ProtocolVersion.V1,
                ["active_collection_id"] = activeId,
                ["active_collection_name"] = ActiveCollectionName(),
            });

            var buffer = new byte[8 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    // 客户端发来的 hello / pong / ack / push_ops 只读不处理：
                    // op 推送走 HTTP（v1 行为，向量也钉住了「服务端不发 ops」）。
                }
            }
            catch (WebSocketException)
            {
                // 对端断了。
            }
            finally
            {
                Hub.Remove(deviceId, socket);
            }
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
