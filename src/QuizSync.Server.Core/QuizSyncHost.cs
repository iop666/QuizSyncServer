using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace QuizSync.Server.Core;

/// <summary>
/// 一个正在跑的 Host（内嵌 Kestrel）。
///
/// 分层纪律（见 <c>src/README.md</c>）：Core **不读命令行参数、不写控制台**；
/// 它只知道「绑定哪个地址、监听哪个端口、报什么版本」。CLI 与桌面端都只是它的
/// 宿主，行为必须能在进程内用回环地址测（<see cref="StartAsync"/> 传端口 0 即可）。
/// </summary>
public sealed class QuizSyncHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private QuizSyncHost(WebApplication app, ServerOptions options, int port)
    {
        _app = app;
        Options = options;
        Port = port;
    }

    public ServerOptions Options { get; }

    /// <summary>实际监听端口（<see cref="ServerOptions.PreferredPort"/> 为 0 时由系统分配）。</summary>
    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// 启动并监听。端口占用时按 <see cref="ServerOptions.PortRange"/> 向上探测；
    /// 全占满则抛 <see cref="InvalidOperationException"/>（**不静默换端口**，
    /// 与 v1 的桌面端行为一致：静默换端口会让二维码里的地址失效）。
    /// </summary>
    public static async Task<QuizSyncHost> StartAsync(
        ServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PreferredPort == 0)
        {
            return await BindAsync(options, 0, cancellationToken).ConfigureAwait(false);
        }

        for (var offset = 0; offset < options.PortRange; offset++)
        {
            var port = options.PreferredPort + offset;
            try
            {
                return await BindAsync(options, port, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (offset < options.PortRange - 1)
            {
                // 端口被占用：试下一个（最后一轮不再吞异常，交给调用方看到真实原因）。
            }
        }

        throw new InvalidOperationException(
            $"端口 {options.PreferredPort}–{options.PreferredPort + options.PortRange - 1} 全部被占用");
    }

    private static async Task<QuizSyncHost> BindAsync(
        ServerOptions options, int port, CancellationToken cancellationToken)
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
        var host = new QuizSyncHost(app, options, port);
        host.MapEndpoints();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        // 端口 0 时系统分配的真实端口只能从服务器特性里读回来。
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        var bound = addresses?.Addresses.FirstOrDefault();
        if (bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out var uri))
        {
            return new QuizSyncHost(app, options, uri.Port);
        }

        return host;
    }

    private void MapEndpoints()
    {
        var json = new JsonSerializerOptions { WriteIndented = false };

        // 运维探针：不属于协议，故意放在 /health（不带版本前缀）。
        _app.MapGet("/health", () =>
            Results.Json(ServerInfo.Health(Options, _uptime.Elapsed), json));

        _app.MapGet("/api/v2/info", () =>
            Results.Json(ServerInfo.ForV2(Options), json));

        // v1 兼容层：老客户端（1.x）只认这个路径与 protocol_version=1。
        _app.MapGet("/api/v1/info", () =>
            Results.Json(ServerInfo.ForV1(Options), json));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
