using System.Globalization;
using QuizSync.Server.Core;

// CLI 只做三件事：解析参数、起 Host、把结果打到终端。
// **任何协议逻辑都不许写在这里**（见 src/README.md 的分层纪律）。

var parsed = CliArgs.Parse(args);
if (parsed.ShowHelp)
{
    Console.WriteLine(CliArgs.HelpText);
    return 0;
}

if (parsed.ShowVersion)
{
    Console.WriteLine($"QuizSync Server {ServerOptions.ProtocolVersion}.0.0 (protocol v{ServerOptions.ProtocolVersion}，含 v1 兼容层)");
    return 0;
}

var options = new ServerOptions
{
    PreferredPort = parsed.Port ?? ServerOptions.DefaultPort,
    BindAddress = parsed.Bind ?? "0.0.0.0",
    DataDirectory = parsed.DataDirectory,
};

try
{
    await using var host = await QuizSyncHost.StartAsync(options);
    Console.WriteLine($"QuizSync Server 已启动：{host.BaseUrl}");
    Console.WriteLine($"协议 v{ServerOptions.ProtocolVersion}（同时在 /api/v1/* 上提供兼容层）");
    if (parsed.Doctor)
    {
        Console.WriteLine("doctor：监听正常、/health 可用");
        return 0;
    }

    Console.WriteLine("按 Ctrl+C 退出。");
    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.TrySetResult();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
    await stop.Task;
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"启动失败：{ex.Message}");
    return 2;
}

internal sealed record CliArgs(
    int? Port, string? Bind, string? DataDirectory, bool Doctor, bool ShowHelp, bool ShowVersion)
{
    public const string HelpText = """
        QuizSync Server —— 局域网搜题的 Host（协议 v2，含 v1 兼容层）

        用法：quizsync-server [选项]

          --port <n>      监听端口（默认 8765，被占用时向上探测到 8770）
          --bind <addr>   绑定地址（默认 0.0.0.0，即局域网可连）
          --data <dir>    数据目录（库 / 图片 / 日志）
          --doctor        启动自检后立即退出（给安装脚本用）
          --version       打印版本
          -h, --help      显示本帮助
        """;

    public static CliArgs Parse(string[] args)
    {
        int? port = null;
        string? bind = null;
        string? data = null;
        var doctor = false;
        var help = false;
        var version = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--bind" when i + 1 < args.Length:
                    bind = args[++i];
                    break;
                case "--data" when i + 1 < args.Length:
                    data = args[++i];
                    break;
                case "--doctor":
                    doctor = true;
                    break;
                case "--version":
                    version = true;
                    break;
                case "-h" or "--help":
                    help = true;
                    break;
                default:
                    break;
            }
        }

        return new CliArgs(port, bind, data, doctor, help, version);
    }
}
