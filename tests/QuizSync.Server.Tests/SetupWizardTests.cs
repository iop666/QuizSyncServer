using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace QuizSync.Server.Tests;

/// <summary>
/// `setup` 向导：**默认 dry-run（只打印计划）**，`--apply` 才落盘。
///
/// 一台真机上第一次跑，最怕的就是「想看一眼却把环境改了」—— 所以默认不写，
/// 且落盘动作要幂等（跑两次不该坏）。CLI 一律当**独立进程**跑（参数解析与工作目录
/// 是最容易出问题的地方，只调函数测不出来）。
/// </summary>
public sealed class SetupWizardTests
{
    private static string CliDll()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, "src", "QuizSync.Server.Cli", "bin", "Release", "net10.0", "QuizSync.Server.Cli.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(probe.FullName, "src", "QuizSync.Server.Cli", "bin", "Debug", "net10.0", "QuizSync.Server.Cli.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            probe = probe.Parent;
        }

        throw new FileNotFoundException("找不到 CLI 产物（先 dotnet build src/QuizSync.Server.Cli）");
    }

    private static string Dotnet() => Environment.GetEnvironmentVariable("QS_DOTNET")
        ?? throw new InvalidOperationException("请设置 QS_DOTNET 指向 dotnet 可执行文件");

    private static (string Output, int ExitCode) RunCli(params string[] args)
    {
        var info = new ProcessStartInfo(Dotnet()) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(CliDll());
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (output, process.ExitCode);
    }

    [Fact]
    public void Dry_run_prints_the_plan_and_changes_nothing()
    {
        var dataDir = Path.Combine(Directory.CreateTempSubdirectory("qs-setup-dry-").FullName, "新目录");

        var (output, code) = RunCli("setup", "--data", dataDir);

        Assert.Equal(0, code);
        Assert.Contains("dry-run", output, StringComparison.Ordinal);
        Assert.Contains("配对码", output, StringComparison.Ordinal);
        // 关键：dry-run **不能**建目录、不能建库。
        Assert.False(Directory.Exists(dataDir), "dry-run 竟然创建了数据目录");
    }

    [Fact]
    public void Apply_creates_the_data_directory_and_writes_settings()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-setup-apply-").FullName;

        var (output, code) = RunCli("setup", "--apply", "--data", dataDir, "--port", "8899");

        Assert.Equal(0, code);
        Assert.Contains("已就绪", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dataDir, "quizsync.db")), "库没建出来");

        // 端口确实写进了设置（用 config get 读回来，走的是另一条命令路径）。
        var (portOut, portCode) = RunCli("config", "get", "pairing.port", "--data", dataDir);
        Assert.Equal(0, portCode);
        Assert.Contains("8899", portOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-setup-twice-").FullName;

        var first = RunCli("setup", "--apply", "--data", dataDir, "--port", "8901");
        var second = RunCli("setup", "--apply", "--data", dataDir, "--port", "8901");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("已就绪", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_output_is_machine_readable()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-setup-json-").FullName;

        var (output, code) = RunCli("setup", "--data", dataDir, "--json", "--port", "8902");

        Assert.Equal(0, code);
        var json = JsonNode.Parse(output.Substring(output.IndexOf('{', StringComparison.Ordinal)))!;
        Assert.False(json["applied"]!.GetValue<bool>());
        Assert.Equal(8902, json["port"]!.GetValue<int>());
        Assert.True(json["steps"]!.AsArray().Count >= 3);
    }

    [Fact]
    public void Help_lists_the_setup_command()
    {
        var (output, code) = RunCli("--help");
        Assert.Equal(0, code);
        Assert.Contains("setup", output, StringComparison.Ordinal);
        Assert.Contains("--apply", output, StringComparison.Ordinal);
    }
}
