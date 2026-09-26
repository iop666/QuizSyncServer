using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuizSync.Server.Core;
using QuizSync.Server.Core.Storage;

// CLI 只做三件事：解析参数、调用 Core 或打**本机控制面**、把结果打到终端。
// **任何协议逻辑都不许写在这里**（见 src/README.md 的分层纪律）。

var parsed = CliArgs.Parse(args);
if (parsed.ShowHelp)
{
    Console.WriteLine(CliArgs.HelpText);
    return 0;
}

var options = new ServerOptions
{
    PreferredPort = parsed.Port ?? ServerOptions.DefaultPort,
    BindAddress = parsed.Bind ?? "0.0.0.0",
    DataDirectory = parsed.DataDirectory,
};

// 中文输出必须显式设成 UTF-8：默认跟随控制台代码页，CI runner 上会变成 ??????
// （实测：本地绿、CI 红的 5 条用例全是这个原因 —— 断言里的中文子串在子进程输出里不存在了）。
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
    // 没有控制台（被重定向到管道）时可能抛，忽略即可。
}

var dataDir = parsed.DataDirectory ?? Path.Combine(AppContext.BaseDirectory, "userdata");

// 免数据目录的命令：`version` / `help` 不该有副作用；
// `setup` 不带 `--apply` 时是 **dry-run** —— 只打印计划，绝不能把目录建出来
// （这条是 `SetupWizardTests.Dry_run_prints_the_plan_and_changes_nothing` 抓出来的：
//  原来无条件建目录，等于「想看一眼却把环境改了」）。
var touchesData = parsed.Command is not ("version" or "help")
    && !(parsed.Command == "setup" && !parsed.Apply)
    && !parsed.ShowHelp;
var controlToken = touchesData ? ReadOrCreateControlToken(dataDir) : string.Empty;

switch (parsed.Command)
{
    case "version":
        Console.WriteLine($"QuizSync Server {ServerOptions.ProtocolVersion}.0.0" +
                          $"（协议 v{ServerOptions.ProtocolVersion}，含 /api/v1 兼容层）");
        return 0;

    case "pair":
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.Add("X-QS-Control", controlToken);
            try
            {
                var json = await client.GetFromJsonAsync<JsonElement>(
                    $"http://127.0.0.1:{options.PreferredPort}/api/v1/pair/code");
                if (parsed.Json)
                {
                    Console.WriteLine(json.GetRawText());
                }
                else
                {
                    var expires = DateTimeOffset
                        .FromUnixTimeMilliseconds(json.GetProperty("expires_at").GetInt64())
                        .ToLocalTime();
                    Console.WriteLine($"配对码：{json.GetProperty("code").GetString()}");
                    Console.WriteLine($"有效期至：{expires:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"配对链接：{json.GetProperty("pair_uri").GetString()}");
                }

                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"读不到配对码：服务没在 127.0.0.1:{options.PreferredPort} 上跑（{ex.Message}）");
                return 3;
            }
        }

    case "devices":
        {
            using var database = QuizSyncDatabase.Open(Path.Combine(dataDir, "quizsync.db"));
            var devices = new DeviceRepository(database);
            if (parsed.Sub == "revoke")
            {
                if (parsed.Arg is null)
                {
                    Console.Error.WriteLine("用法：devices revoke <device_id>");
                    return 1;
                }

                devices.Revoke(parsed.Arg, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Console.WriteLine($"已吊销：{parsed.Arg}");
                return 0;
            }

            var rows = devices.List();
            if (parsed.Json)
            {
                var array = new JsonArray();
                foreach (var d in rows)
                {
                    array.Add(new JsonObject
                    {
                        ["device_id"] = d.DeviceId,
                        ["name"] = d.Name,
                        ["platform"] = d.Platform,
                        ["paired_at"] = d.PairedAt,
                        ["revoked"] = d.IsRevoked,
                    });
                }

                Console.WriteLine(array.ToJsonString());
                return 0;
            }

            if (rows.Count == 0)
            {
                Console.WriteLine("（还没有已配对设备）");
                return 0;
            }

            foreach (var d in rows)
            {
                Console.WriteLine($"{d.DeviceId,-24} {d.Name,-16} {d.Platform,-8} {(d.IsRevoked ? "已吊销" : "有效")}");
            }

            return 0;
        }

    case "config":
        {
            using var database = QuizSyncDatabase.Open(Path.Combine(dataDir, "quizsync.db"));
            var store = new DeviceRepository(database);
            if (parsed.Sub == "set" && parsed.Arg is not null && parsed.Value is not null)
            {
                store.SetSetting(parsed.Arg, parsed.Value);
                Console.WriteLine($"{parsed.Arg} = {parsed.Value}");
                return 0;
            }

            if (parsed.Sub == "get" && parsed.Arg is not null)
            {
                Console.WriteLine(store.GetSetting(parsed.Arg) ?? "（未设置）");
                return 0;
            }

            if (parsed.Json)
            {
                Console.WriteLine(new JsonObject
                {
                    ["data_dir"] = dataDir,
                    ["port"] = options.PreferredPort,
                    ["port_range"] = options.PortRange,
                    ["active_collection_id"] = store.GetSetting("active_collection_id"),
                }.ToJsonString());
                return 0;
            }

            Console.WriteLine($"data_dir = {dataDir}");
            Console.WriteLine($"active_collection_id = {store.GetSetting("active_collection_id") ?? "（未设置）"}");
            Console.WriteLine($"pairing.port = {options.PreferredPort}（占用时向上探测到 {options.PreferredPort + options.PortRange - 1}）");
            return 0;
        }

    case "setup":
        {
            // 向导：**默认只打印计划（dry-run）**，`--apply` 才落盘。
            // 一台真机上第一次跑，最怕的就是「想看一眼却把环境改了」。
            var port = options.PreferredPort;
            var bind = options.BindAddress;
            var databasePath = Path.Combine(dataDir, "quizsync.db");

            var steps = new List<(string Action, string Detail)>
            {
                ("确保数据目录存在", dataDir),
                ("写入端口与绑定地址", $"pairing.port = {port}；bind = {bind}"),
                ("初始化库（表结构与协议 schema 一致）", databasePath),
            };

            if (parsed.Json)
            {
                var stepsJson = new System.Text.Json.Nodes.JsonArray();
                foreach (var (action, detail) in steps)
                {
                    stepsJson.Add(new JsonObject { ["action"] = action, ["detail"] = detail });
                }

                Console.WriteLine(new JsonObject
                {
                    ["applied"] = parsed.Apply,
                    ["data_dir"] = dataDir,
                    ["port"] = port,
                    ["bind"] = bind,
                    ["steps"] = stepsJson,
                }.ToJsonString());
            }
            else
            {
                Console.WriteLine(parsed.Apply ? "将执行以下步骤：" : "计划（dry-run，加 --apply 才会真的做）：");
                foreach (var (action, detail) in steps)
                {
                    Console.WriteLine($"  · {action}：{detail}");
                }

                if (!parsed.Apply)
                {
                    Console.WriteLine("落盘后请执行：QuizSync.Server.Cli pair   # 生成配对码，供手机扫码");
                }
            }

            if (!parsed.Apply)
            {
                return 0;
            }

            Directory.CreateDirectory(dataDir);
            using (var database = QuizSyncDatabase.Open(databasePath))
            {
                var store = new DeviceRepository(database);
                store.SetSetting("pairing.port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                store.SetSetting("pairing.bind", bind);
            }

            if (!parsed.Json)
            {
                Console.WriteLine($"已就绪：{dataDir}");
                Console.WriteLine("下一步：run 启动服务端；pair 生成配对码。");
            }

            return 0;
        }

    case "doctor":
        {
            var checks = new List<(string Name, bool Ok, string Detail)>
        {
            ("数据目录", true, dataDir),
            ("控制令牌", controlToken.Length == 64, controlToken.Length == 64 ? "已生成（仅本机可用）" : "形态不对"),
        };

            try
            {
                using var database = QuizSyncDatabase.Open(Path.Combine(dataDir, "quizsync.db"));
                var devices = new DeviceRepository(database);
                checks.Add(("本地库", true, $"schema v{V1Schema.SchemaVersion}，设备 {devices.List().Count} 台"));
            }
            catch (Exception ex)
            {
                checks.Add(("本地库", false, ex.Message));
            }

            try
            {
                await using var host = await QuizSyncHost.StartAsync(options);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var health = await client.GetFromJsonAsync<JsonElement>($"{host.BaseUrl}/health");
                checks.Add(("监听", true, $"{host.BaseUrl}（/health: {health.GetProperty("status").GetString()}）"));
                checks.Add(("配对码", host.Pairing.Code.Length == 6, host.Pairing.Code));
            }
            catch (Exception ex)
            {
                checks.Add(("监听", false, ex.Message));
            }

            var failed = checks.Count(c => !c.Ok);
            if (parsed.Json)
            {
                var array = new JsonArray();
                foreach (var (name, ok, detail) in checks)
                {
                    array.Add(new JsonObject { ["check"] = name, ["ok"] = ok, ["detail"] = detail });
                }

                Console.WriteLine(array.ToJsonString());
            }
            else
            {
                foreach (var (name, ok, detail) in checks)
                {
                    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name,-8} {detail}");
                }
            }

            return failed == 0 ? 0 : 2;
        }

    case "run":
    default:
        {
            try
            {
                await using var host = await QuizSyncHost.StartAsync(options with { ControlToken = controlToken });
                Console.WriteLine($"QuizSync Server 已启动：{host.BaseUrl}");
                Console.WriteLine($"协议 v{ServerOptions.ProtocolVersion}（同时在 /api/v1/* 上提供兼容层）");
                Console.WriteLine($"配对码：{host.Pairing.Code}（有效期 5 分钟，`pair` 命令可随时再读）");
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
        }
}

/// <summary>
/// 本机控制令牌：第一次跑时生成并写进数据目录（CLI 与正在跑的实例靠它对话）。
/// 控制面只服务回环地址，所以这个令牌不必联网传输。
/// </summary>
static string ReadOrCreateControlToken(string dataDir)
{
    Directory.CreateDirectory(dataDir);
    var path = Path.Combine(dataDir, "control.token");
    if (File.Exists(path))
    {
        var existing = File.ReadAllText(path).Trim();
        if (existing.Length == 64)
        {
            return existing;
        }
    }

    var token = QuizSync.Server.Core.Tokens.NewToken();
    File.WriteAllText(path, token);
    return token;
}

internal sealed record CliArgs(
    string Command,
    string? Sub,
    string? Arg,
    string? Value,
    int? Port,
    string? Bind,
    string? DataDirectory,
    bool Doctor,
    bool Json,
    bool Apply,
    bool ShowHelp)
{
    public const string HelpText = """
        QuizSync Server —— 局域网搜题的 Host（协议 v2，含 v1 兼容层）

        用法：quizsync-server [命令] [选项]

        命令：
          run                 前台运行（默认）
          setup [--apply]     首次配置向导（默认 dry-run，只打印计划）
          doctor              自检：数据目录 / 本地库 / 监听 / 配对码
          pair                读正在运行的实例的配对码（含配对链接与有效期）
          devices [list]      列出已配对设备
          devices revoke <id> 吊销设备
          config [list]       打印数据目录与端口等配置
          config get <key>    读一个设置项
          config set <k> <v>  写一个设置项
          version             打印版本
          help                显示本帮助

        选项：
          --port <n>      监听端口（默认 8765，被占用时向上探测到 8770）
          --bind <addr>   绑定地址（默认 0.0.0.0，即局域网可连）
          --data <dir>    数据目录（库 / 图片 / 日志 / control.token）
          --json          机器可读输出（脚本用）
          --apply         setup 真的落盘（不加则只打印计划）
          --doctor        等价于 doctor 命令
          --version       等价于 version 命令
          -h, --help      显示本帮助
        """;

    public static CliArgs Parse(string[] args)
    {
        var positional = new List<string>();
        int? port = null;
        string? bind = null;
        string? data = null;
        var doctor = false;
        var apply = false;
        var json = false;
        var help = false;

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
                case "--json":
                    json = true;
                    break;
                case "--doctor":
                    doctor = true;
                    break;
                case "--version":
                    positional.Add("version");
                    break;
                case "-h" or "--help":
                    help = true;
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        var command = positional.Count > 0 ? positional[0] : "run";
        if (args.Contains("--apply"))
        {
            apply = true;
        }

        if (doctor)
        {
            command = "doctor";
        }

        return new CliArgs(
            Command: command,
            Sub: positional.Count > 1 ? positional[1] : null,
            Arg: positional.Count > 2 ? positional[2] : null,
            Value: positional.Count > 3 ? positional[3] : null,
            Port: port,
            Bind: bind,
            DataDirectory: data,
            Doctor: doctor,
            Apply: apply,
            Json: json,
            ShowHelp: help);
    }
}
