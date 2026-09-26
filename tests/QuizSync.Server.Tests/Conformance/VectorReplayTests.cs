using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QuizSync.Server.Core;
using Xunit.Sdk;

namespace QuizSync.Server.Tests.Conformance;

/// <summary>
/// 一致性向量的 **C# 回放器**（格式见 `QuizSyncProtocol/conformance/README.md`）。
///
/// **纪律**：只做 I/O 适配。用 <see cref="HttpClient"/> 打真 HTTP，断言值全部来自向量文件，
/// **不引用 Core 的业务服务去断言**。回放器与 Host 的接口只有四个：
/// 起 Host（拿到端口）、读 <c>host.Pairing.Code</c> 当 `$pairingCode`、调
/// <c>host.Pairing.RefreshCode()</c> 表达「UI 上的刷新」、用
/// <c>host.Database.Connection</c> 跑 `seed` 的 SQL。
///
/// **未实现的操作 / 断言 / 字段一律失败**（带「第几步 + 标题」），不许静默跳过 ——
/// 宁可红着，也不要假的绿。
///
/// 分组开关：<see cref="SupportedGroups"/> 里的分组**必须真跑且必须全绿**（只为它们生成测试），
/// 其余分组只在 <see cref="UnimplementedGroups"/> 里显式声明并写明原因；
/// <see cref="VectorInventory_is_fully_accounted_for"/> 保证「向量目录里多出一组、
/// 或悄悄少写一句原因」都会变红。
/// </summary>
public sealed class VectorReplayTests
{
    /// <summary>本 Host 已经实现、因而**必须真跑且必须全绿**的向量分组。</summary>
    private static readonly string[] SupportedGroups = ["auth.ndjson", "images.ndjson", "errors.ndjson", "tasks.ndjson", "sync.ndjson"];

    /// <summary>
    /// 尚未实现的分组：**显式声明 + 一句原因**（原因是逐条实测出来的失败位置，
    /// 不是猜的）。改动这批名单时，必须**真的跑一遍**再写。
    ///
    /// 现在为空：v1 兼容层的五组 101 步已全部实测通过。将来 `/api/v2/*` 落地后，
    /// v2 向量组要在这里显式登记，或者直接加进 <see cref="SupportedGroups"/>。
    /// </summary>
    private static readonly Dictionary<string, string> UnimplementedGroups = new(StringComparer.Ordinal);

    /// <summary>xUnit 在发现阶段调用：把支持的分组变成一个个测试用例。</summary>
    public static TheoryData<string> Supported_groups()
    {
        var data = new TheoryData<string>();
        foreach (var group in SupportedGroups)
        {
            data.Add(group);
        }

        return data;
    }

    /// <summary>
    /// 回放一个支持的分组：**每个文件一套干净的服务端 + 一个可控时钟**，
    /// 步骤在同一台服务端上按顺序执行。
    /// </summary>
    [Theory]
    [MemberData(nameof(Supported_groups))]
    public async Task Replay_supported_vector_group(string fileName)
    {
        var directory = NdjsonVector.ResolveDirectory();
        Assert.True(
            Directory.Exists(directory),
            $"找不到一致性向量目录：{directory}\n" +
            $"请设置环境变量 {NdjsonVector.DirectoryVariable} 指向 QuizSyncProtocol/conformance/vectors");

        var file = Path.Combine(directory, fileName);
        Assert.True(File.Exists(file), $"声明为「已支持」的向量分组不存在：{file}");

        var steps = NdjsonVector.Load(file);
        Assert.NotEmpty(steps);

        await using var replay = await VectorReplay.StartAsync(fileName);
        await replay.RunAsync(steps);
    }

    /// <summary>向量目录必须存在、且不能是空的（**不接受静默跳过**）。</summary>
    [Fact]
    public void Vector_directory_must_exist()
    {
        var directory = NdjsonVector.ResolveDirectory();
        Assert.True(
            Directory.Exists(directory),
            $"找不到一致性向量目录：{directory}\n" +
            $"请设置环境变量 {NdjsonVector.DirectoryVariable} 指向 QuizSyncProtocol/conformance/vectors");

        var files = NdjsonVector.ListVectorFiles(directory);
        Assert.True(files.Count > 0, $"{directory} 里没有 *.ndjson");
    }

    /// <summary>
    /// 向量目录里的每个 `*.ndjson` 都必须**被归类**：要么在 `SupportedGroups`（会真跑），
    /// 要么在 `UnimplementedGroups`（写明原因）。这样「悄悄少跑一组」是不可能的：
    /// 上游往向量目录里加文件、或把已支持的分组改名，这条测试就会红。
    /// </summary>
    [Fact]
    public void VectorInventory_is_fully_accounted_for()
    {
        var directory = NdjsonVector.ResolveDirectory();
        Assert.True(
            Directory.Exists(directory),
            $"找不到一致性向量目录：{directory}\n" +
            $"请设置环境变量 {NdjsonVector.DirectoryVariable} 指向 QuizSyncProtocol/conformance/vectors");

        var onDisk = NdjsonVector.ListVectorFiles(directory);
        Assert.NotEmpty(onDisk);

        var supported = SupportedGroups.ToHashSet(StringComparer.Ordinal);

        // 名单自身的一致性：不许重叠、不许出现磁盘上没有的幽灵条目。
        Assert.Empty(supported.Intersect(UnimplementedGroups.Keys, StringComparer.Ordinal));
        Assert.Empty(supported.Except(onDisk, StringComparer.Ordinal));
        Assert.Empty(UnimplementedGroups.Keys.Except(onDisk, StringComparer.Ordinal));

        // 未实现名单必须带原因（空原因等于没写）。
        Assert.All(
            UnimplementedGroups,
            entry => Assert.False(
                string.IsNullOrWhiteSpace(entry.Value), $"{entry.Key} 写进了未实现名单却没写原因"));

        var unaccounted = onDisk
            .Where(name => !supported.Contains(name) && !UnimplementedGroups.ContainsKey(name))
            .ToList();
        Assert.True(
            unaccounted.Count == 0,
            $"向量目录里有分组既不在支持名单、也没写进未实现名单：{string.Join("、", unaccounted)}。" +
            "要么实现它并加进 SupportedGroups，要么在 UnimplementedGroups 里写明原因 —— 不许悄悄少跑一组。");
    }
}

/// <summary>
/// 一次回放的运行环境：一台干净 Host（内存库）+ 可推进的时钟 + 变量表 + 一个 HttpClient。
/// </summary>
internal sealed class VectorReplay : IAsyncDisposable
{
    /// <summary>`do.http` 里回放器认识的字段。**多一个就失败**：不认识的字段被静默忽略 = 假的绿。</summary>
    private static readonly HashSet<string> HttpSpecKeys = new(StringComparer.Ordinal)
    {
        "method", "path", "auth", "json", "raw", "multipart", "headers", "repeat", "content_type",
    };

    /// <summary>`expect` 里回放器认识的断言。多一个就失败（同上）。</summary>
    private static readonly HashSet<string> ExpectKeys = new(StringComparer.Ordinal)
    {
        "status", "json", "json_contains", "json_regex", "capture", "json_absent", "body_sha256",
    };

    /// <summary>`$变量` 的语法（与 Dart 参考回放器同一条正则）。</summary>
    private static readonly Regex VariablePattern = new(
        @"\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>`seed.active_collection` 落库用的键（协议里的「当前选中合集」）。</summary>
    private const string ActiveCollectionSettingKey = "active_collection_id";

    private readonly QuizSyncHost _host;
    private readonly MutableClock _clock;
    private readonly HttpClient _client;
    private readonly string _file;
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);

    private VectorReplay(QuizSyncHost host, MutableClock clock, HttpClient client, string file)
    {
        _host = host;
        _clock = clock;
        _client = client;
        _file = file;
    }

    /// <summary>
    /// 起一套干净环境。参数按 `conformance/README.md` §1：一个文件 = 一台服务端 + 一个客户端。
    ///
    /// 时钟实例**必须与传给 Host 的是同一个**（`clock` 步骤推进的就是它，换一个实例
    /// 服务端那边时间纹丝不动）。`AiConfigured = true` 是**场景前提**：向量自述的主机是
    /// 「已配置 AI」的（auth 组第 1 步断言 `ai_configured=true`），Dart 参考回放器同样给
    /// 服务端塞了一个测试 key。
    /// </summary>
    public static async Task<VectorReplay> StartAsync(string vectorFile)
    {
        var clock = new MutableClock();
        var host = await QuizSyncHost.StartAsync(
            new ServerOptions
            {
                PreferredPort = 0,
                ServerName = "TEST-HOST",
                AppVersion = "1.0.0",
                DeviceId = "server-device-1",
                AiConfigured = true,
            },
            clock);

        return new VectorReplay(
            host, clock, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, vectorFile);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    /// <summary>按顺序回放一个文件的全部步骤。</summary>
    public async Task RunAsync(IReadOnlyList<VectorStep> steps)
    {
        foreach (var step in steps)
        {
            await RunStepAsync($"{_file} 第 {step.Number} 步（{step.Title}）", step);
        }
    }

    private async Task RunStepAsync(string where, VectorStep step)
    {
        // `do` 恰好一个操作：两个操作时按顺序只跑第一个 = 静默漏掉一个动作。
        if (step.Do.Count != 1)
        {
            throw Error($"{where} 的 do 必须恰好一个操作，实际 {step.Do.Count} 个：" +
                $"{string.Join("、", step.Do.Select(pair => pair.Key))}");
        }

        var (operation, spec) = step.Do.First();
        switch (operation)
        {
            case "http":
                await RunHttpAsync(where, RequireObject(spec, where, "do.http"), step.Expect);
                break;
            case "clock":
                RunClock(where, RequireObject(spec, where, "do.clock"));
                break;
            case "server":
                RunServer(where, RequireObject(spec, where, "do.server"));
                break;
            case "seed":
                RunSeed(where, RequireObject(spec, where, "do.seed"));
                break;
            case "poll":
                await RunPollAsync(where, RequireObject(spec, where, "do.poll"), step.Expect);
                break;
            default:
                throw Error($"{where} 用了回放器还没实现的操作：{operation}");
        }
    }

    /// <summary>
    /// `repeat: N` = 同一请求连发 N 次，**每次都按 `expect` 断言**（限流一类
    /// 「同一个动作来很多次」的向量就是这么表达的）。
    /// </summary>
    private async Task RunHttpAsync(string where, JsonObject spec, JsonObject? expect)
    {
        var times = OptionalInt(spec, "repeat", where) ?? 1;
        spec.Remove("repeat");
        if (times < 1)
        {
            throw Error($"{where} 的 repeat 必须 >= 1（0 次等于不跑这个动作）");
        }

        for (var i = 0; i < times; i++)
        {
            AssertResponse(where, expect, await SendAsync(where, spec));
        }
    }

    /// <summary>推进**回放器注入的那个时钟**（配对码过期 / 限流窗口 / 锁定期靠它）。</summary>
    private void RunClock(string where, JsonObject spec)
    {
        RejectUnknownKeys(spec, where, "clock", ["advance_ms"]);
        _clock.Advance(RequireInt(spec["advance_ms"], where, "clock.advance_ms"));
    }

    /// <summary>`server: {"refresh_pairing_code": true}` = UI 上的「刷新」（换码并重置有效期）。</summary>
    private void RunServer(string where, JsonObject spec)
    {
        RejectUnknownKeys(spec, where, "server", ["refresh_pairing_code"]);
        if (spec["refresh_pairing_code"] is not JsonValue flag ||
            !flag.TryGetValue<bool>(out var refresh) ||
            !refresh)
        {
            throw Error($"{where} 的 server.refresh_pairing_code 只支持 true");
        }

        _host.Pairing.RefreshCode();
    }

    /// <summary>
    /// 回放器侧的初始状态：主机库里的合集 / 当前选中合集 / 已排队任务数。
    /// **这是回放器唯一被允许直接写库的地方**（其余一律走 HTTP）。
    /// </summary>
    private void RunSeed(string where, JsonObject spec)
    {
        RejectUnknownKeys(spec, where, "seed", ["collection", "active_collection", "queued_tasks"]);

        if (spec["collection"] is JsonNode collectionNode)
        {
            var collection = RequireObject(collectionNode, where, "seed.collection");
            var id = RequireString(collection, "id", where);
            var name = OptionalString(collection, "name", where) ?? string.Empty;
            var now = _clock.NowMs;

            using var command = _host.Database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO collections (collection_id, name, created_at, updated_at, updated_by, lamport)
                VALUES ($id, $name, $now, $now, $by, 0)
                ON CONFLICT(collection_id) DO UPDATE SET
                    name = excluded.name,
                    updated_at = excluded.updated_at,
                    deleted_at = NULL
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$by", _host.Options.DeviceId);
            command.ExecuteNonQuery();
        }

        if (spec["active_collection"] is JsonNode activeNode)
        {
            var active = ScalarText(Resolve(activeNode, where));
            using var command = _host.Database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            command.Parameters.AddWithValue("$key", ActiveCollectionSettingKey);
            command.Parameters.AddWithValue("$value", active);
            command.ExecuteNonQuery();
        }

        if (spec["queued_tasks"] is JsonNode queuedNode)
        {
            var queued = RequireInt(queuedNode, where, "seed.queued_tasks");
            var now = _clock.NowMs;

            using var command = _host.Database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tasks (task_id, image_hash, source_device, status, created_at)
                VALUES ($id, $hash, 'seed', 'queued', $now)
                ON CONFLICT(task_id) DO NOTHING
                """;
            var idParameter = command.Parameters.AddWithValue("$id", string.Empty);
            var hashParameter = command.Parameters.AddWithValue("$hash", string.Empty);
            command.Parameters.AddWithValue("$now", now);
            for (var i = 0; i < queued; i++)
            {
                idParameter.Value = $"seed-task-{i}";
                hashParameter.Value = $"seed-hash-{i}";
                command.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// 轮询一个只读端点直到 `until` 的局部匹配成立（或超时失败），
    /// 随后按外层 `expect` 断言**最后一次**响应。
    ///
    /// 用于「任务从 queued 走到 done」这类异步收敛：向量只需要写清**等到什么**与
    /// **最终期望什么**，不用关心要等几轮。
    /// </summary>
    private async Task RunPollAsync(string where, JsonObject spec, JsonObject? expect)
    {
        var until = spec["until"] is JsonNode untilNode ? Resolve(untilNode, where) : null;
        var maxMs = OptionalInt(spec, "max_ms", where) ?? 15000;
        var intervalMs = OptionalInt(spec, "interval_ms", where) ?? 200;

        var httpSpec = (JsonObject)spec.DeepClone();
        httpSpec.Remove("until");
        httpSpec.Remove("max_ms");
        httpSpec.Remove("interval_ms");

        var deadline = Environment.TickCount64 + maxMs;
        ReplayResponse? last = null;
        while (Environment.TickCount64 < deadline)
        {
            last = await SendAsync(where, httpSpec);
            if (until is null || IsMatch(until, last.Body))
            {
                break;
            }

            await Task.Delay(intervalMs);
        }

        if (last is null)
        {
            throw Error($"{where} 轮询一次都没发出去（max_ms={maxMs}）");
        }

        if (until is not null && !IsMatch(until, last.Body))
        {
            throw Error($"{where} 轮询超时（{maxMs}ms 内 until 未成立）：{last.Text}");
        }

        AssertResponse(where, expect, last);
    }

    /// <summary>发一次真实 HTTP（不做断言），并把响应按「状态码 / JSON / 原始字节」交给断言。</summary>
    private async Task<ReplayResponse> SendAsync(string where, JsonObject spec)
    {
        foreach (var key in spec.Select(pair => pair.Key))
        {
            if (!HttpSpecKeys.Contains(key))
            {
                throw Error($"{where} 的 http 请求有回放器不认识的字段：{key}");
            }
        }

        var method = new HttpMethod(RequireString(spec, "method", where));
        var path = Substitute(RequireString(spec, "path", where), where);
        var uri = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(path, UriKind.Absolute)
            : new Uri(_host.BaseUrl + (path.StartsWith('/') ? path : "/" + path), UriKind.Absolute);

        using var request = new HttpRequestMessage(method, uri);
        ApplyAuth(request, OptionalString(spec, "auth", where) ?? "none", where);
        ApplyHeaders(request, spec["headers"], where);
        request.Content = BuildBody(spec, where);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request);
        }
        catch (HttpRequestException ex)
        {
            // 服务端在客户端还在发的时候关连接（Windows 会发 RST）也走这里 ——
            // 报出是哪一步，别让排查从「Connection closed」开始猜。
            throw Error($"{where} 请求失败：{ex.Message}");
        }

        using (response)
        {
            var raw = await response.Content.ReadAsByteArrayAsync();
            var text = Encoding.UTF8.GetString(raw);
            return new ReplayResponse((int)response.StatusCode, ParseJson(text), raw, text);
        }
    }

    /// <summary>`auth` 取值：`none` / `device` / `device:&lt;名字&gt;` / `bogus` / `empty` / `malformed`。</summary>
    private void ApplyAuth(HttpRequestMessage request, string auth, string where)
    {
        string? header = auth switch
        {
            "none" => null,
            "bogus" => "Bearer " + new string('0', 64),
            // 形如 `Authorization: Bearer `（令牌为空）
            "empty" => "Bearer ",
            // 没有 Bearer 前缀
            "malformed" => "Token abc",
            "device" => "Bearer " + Variable("token", where),
            _ when auth.StartsWith("device:", StringComparison.Ordinal) =>
                "Bearer " + Variable(auth["device:".Length..], where),
            _ => throw Error($"{where} 的 auth 取值不认识：{auth}"),
        };

        if (header is not null && !request.Headers.TryAddWithoutValidation("Authorization", header))
        {
            throw Error($"{where} 无法设置 Authorization 头");
        }
    }

    /// <summary>`headers` 用于版本协商一类用例（`X-QS-Client-Version`）。</summary>
    private void ApplyHeaders(HttpRequestMessage request, JsonNode? node, string where)
    {
        if (node is null)
        {
            return;
        }

        foreach (var (name, value) in RequireObject(node, where, "http.headers"))
        {
            var text = ScalarText(Resolve(value, where));
            if (!request.Headers.TryAddWithoutValidation(name, text))
            {
                throw Error($"{where} 无法设置请求头 {name}（值：{text}）");
            }
        }
    }

    /// <summary>`multipart`（合成指定长度的字节，测体积上限）/ `json` / `raw`（原样字符串体）。</summary>
    private HttpContent? BuildBody(JsonObject spec, string where)
    {
        if (spec["multipart"] is JsonNode multipartNode)
        {
            var multipart = RequireObject(multipartNode, where, "http.multipart");
            var length = RequireInt(multipart["bytes"], where, "http.multipart.bytes");
            if (length < 0)
            {
                throw Error($"{where} 的 http.multipart.bytes 不能是负数：{length}");
            }

            var field = OptionalString(multipart, "field", where) ?? "file";
            var filename = OptionalString(multipart, "filename", where) ?? "x.jpg";

            // 与 Dart 参考回放器同形：一段 'A'（0x41）填充的字节，part 的 Content-Type 固定 image/jpeg。
            var payload = new byte[length];
            Array.Fill(payload, (byte)'A');
            var file = new ByteArrayContent(payload);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

            var form = new MultipartFormDataContent();
            form.Add(file, field, filename);
            return form;
        }

        if (spec["json"] is JsonNode jsonNode)
        {
            var body = Resolve(jsonNode, where)?.ToJsonString() ?? "null";
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (spec["raw"] is JsonNode rawNode)
        {
            var raw = ScalarText(Resolve(rawNode, where));
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(raw));
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                OptionalString(spec, "content_type", where) ?? "application/json");
            return content;
        }

        return null;
    }

    /// <summary>断言一个响应（`where` 用于失败信息：第几步 + 标题）。</summary>
    private void AssertResponse(string where, JsonObject? expect, ReplayResponse got)
    {
        if (expect is null)
        {
            return;
        }

        foreach (var key in expect.Select(pair => pair.Key))
        {
            if (!ExpectKeys.Contains(key))
            {
                throw Error($"{where} 的 expect 有回放器不认识的断言：{key}");
            }
        }

        var body = got.Body;

        if (expect["status"] is JsonNode statusNode)
        {
            var expectedStatus = RequireInt(statusNode, where, "expect.status");
            if (got.Status != expectedStatus)
            {
                throw Error($"{where} 期望 HTTP {expectedStatus}，实际 {got.Status}：{got.Text}");
            }
        }

        if (expect["body_sha256"] is JsonNode hashNode)
        {
            var expected = ScalarText(Resolve(hashNode, where));
            var actual = Convert.ToHexStringLower(SHA256.HashData(got.Raw));
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw Error($"{where} body_sha256 期望 {expected}，实际 {actual}");
            }
        }

        if (expect["json"] is JsonNode jsonNode)
        {
            if (body is null)
            {
                throw Error($"{where} 期望 JSON 响应，实际：{got.Text}");
            }

            Match(Resolve(jsonNode, where), body, $"{where} json", strictArrays: true);
        }

        if (expect["json_contains"] is JsonNode containsNode)
        {
            if (body is null)
            {
                throw Error($"{where} 期望 JSON 响应，实际：{got.Text}");
            }

            Match(Resolve(containsNode, where), body, $"{where} json_contains", strictArrays: false);
        }

        if (expect["json_regex"] is JsonNode regexNode)
        {
            foreach (var (path, patternNode) in RequireObject(regexNode, where, "expect.json_regex"))
            {
                var pattern = ScalarText(Resolve(patternNode, where));
                var actual = Path(body, path);
                var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (actual is null || !regex.IsMatch(ScalarText(actual)))
                {
                    throw Error($"{where} json_regex[{path}] 期望匹配 {pattern}，实际 {Describe(actual)}");
                }
            }
        }

        if (expect["capture"] is JsonNode captureNode)
        {
            foreach (var (name, pathNode) in RequireObject(captureNode, where, "expect.capture"))
            {
                var path = ScalarText(pathNode);
                var value = Path(body, path) ??
                    throw Error($"{where} capture[{name}] 取不到路径 {path}");
                _vars[name] = ScalarText(value);
            }
        }

        if (expect["json_absent"] is JsonNode absentNode)
        {
            if (absentNode is not JsonArray absent)
            {
                throw Error($"{where} 的 expect.json_absent 必须是数组");
            }

            foreach (var pathNode in absent)
            {
                var path = ScalarText(pathNode);
                var value = Path(body, path);
                if (value is not null)
                {
                    throw Error($"{where} json_absent：{path} 不该出现，实际是 {value.ToJsonString()}");
                }
            }
        }
    }

    /// <summary>
    /// 深度局部匹配：对象只要求期望的键存在且递归匹配；`strictArrays` 时数组要求
    /// **长度相等 + 按下标逐项匹配**（所以 `[]` 断言「必须是空数组」），否则只要求包含。
    /// </summary>
    private static void Match(JsonNode? expected, JsonNode? actual, string where, bool strictArrays)
    {
        switch (expected)
        {
            case JsonObject expectedObject:
                if (actual is not JsonObject actualObject)
                {
                    throw Error($"{where}：期望对象，实际 {Describe(actual)}");
                }

                foreach (var (key, value) in expectedObject)
                {
                    Match(value, actualObject[key], $"{where}.{key}", strictArrays);
                }

                return;

            case JsonArray expectedArray:
                if (actual is not JsonArray actualArray)
                {
                    throw Error($"{where}：期望数组，实际 {Describe(actual)}");
                }

                if (strictArrays)
                {
                    if (expectedArray.Count != actualArray.Count)
                    {
                        throw Error(
                            $"{where}：期望 {expectedArray.Count} 项，实际 {actualArray.Count} 项（{actualArray.ToJsonString()}）");
                    }

                    for (var i = 0; i < expectedArray.Count; i++)
                    {
                        Match(expectedArray[i], actualArray[i], $"{where}[{i}]", strictArrays: true);
                    }
                }
                else
                {
                    foreach (var item in expectedArray)
                    {
                        if (!actualArray.Any(candidate => IsMatch(item, candidate)))
                        {
                            throw Error(
                                $"{where}：实际数组里找不到期望项 {item?.ToJsonString()}（实际 {actualArray.ToJsonString()}）");
                        }
                    }
                }

                return;

            default:
                if (!ScalarEquals(expected, actual))
                {
                    throw Error($"{where}：期望 {Describe(expected)}，实际 {Describe(actual)}");
                }

                return;
        }
    }

    /// <summary>「包含」判定里的候选比较：候选内部一律按严格数组比。</summary>
    private static bool IsMatch(JsonNode? expected, JsonNode? actual)
    {
        try
        {
            Match(expected, actual, string.Empty, strictArrays: true);
            return true;
        }
        catch (XunitException)
        {
            return false;
        }
    }

    /// <summary>标量比较（数字按数值比，字符串/布尔严格比；`null` 只与 `null` 相等）。</summary>
    private static bool ScalarEquals(JsonNode? expected, JsonNode? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        if (expected is not JsonValue expectedValue || actual is not JsonValue actualValue)
        {
            return false;
        }

        if (Number(expectedValue, out var left) && Number(actualValue, out var right))
        {
            return left == right;
        }

        if (expectedValue.TryGetValue<string>(out var expectedText) &&
            actualValue.TryGetValue<string>(out var actualText))
        {
            return string.Equals(expectedText, actualText, StringComparison.Ordinal);
        }

        if (expectedValue.TryGetValue<bool>(out var expectedFlag) &&
            actualValue.TryGetValue<bool>(out var actualFlag))
        {
            return expectedFlag == actualFlag;
        }

        return string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal);
    }

    private static bool Number(JsonValue value, out decimal number) =>
        value.TryGetValue<decimal>(out number);

    /// <summary>
    /// JSON 路径：`a.b`、`a[0].b` 两种写法（前导 `$.` 可有可无）。
    /// 取不到（含中途类型不对、下标越界）返回 <c>null</c>。
    /// </summary>
    private static JsonNode? Path(JsonNode? root, string path)
    {
        var text = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;
        var node = root;

        foreach (var segment in text.Split('.'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];
            if (name.Length > 0)
            {
                if (node is not JsonObject obj)
                {
                    return null;
                }

                node = obj[name];
            }

            var cursor = name.Length;
            while (cursor < segment.Length)
            {
                if (segment[cursor] != '[')
                {
                    return null;
                }

                var close = segment.IndexOf(']', cursor);
                if (close < 0 ||
                    !int.TryParse(
                        segment.AsSpan(cursor + 1, close - cursor - 1),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var index))
                {
                    return null;
                }

                if (node is not JsonArray array || index >= array.Count)
                {
                    return null;
                }

                node = array[index];
                cursor = close + 1;
            }
        }

        return node;
    }

    /// <summary>把 `{"code":"$pairingCode"}` 这类占位符替换成实际值（递归，只在字符串里替换）。</summary>
    private JsonNode? Resolve(JsonNode? node, string where) => node switch
    {
        null => null,
        JsonObject obj => ResolveObject(obj, where),
        JsonArray array => ResolveArray(array, where),
        JsonValue value when value.TryGetValue<string>(out var text) =>
            JsonValue.Create(Substitute(text, where)),
        _ => node.DeepClone(),
    };

    private JsonObject ResolveObject(JsonObject source, string where)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in source)
        {
            copy[key] = Resolve(value, where);
        }

        return copy;
    }

    private JsonArray ResolveArray(JsonArray source, string where)
    {
        var copy = new JsonArray();
        foreach (var item in source)
        {
            copy.Add(Resolve(item, where));
        }

        return copy;
    }

    private string Substitute(string text, string where) =>
        VariablePattern.Replace(text, match => Variable(match.Groups[1].Value, where));

    /// <summary>
    /// 变量取值：`$pairingCode` **每次使用时实时读**主机当前配对码（刷新后自动跟着变）、
    /// `$port` 实时读监听端口，其余来自 `capture`。
    /// </summary>
    private string Variable(string name, string where) => name switch
    {
        "pairingCode" => _host.Pairing.Code,
        "port" => _host.Port.ToString(CultureInfo.InvariantCulture),
        _ => _vars.TryGetValue(name, out var value)
            ? value
            : throw Error($"{where} 引用了还没有值的变量 ${name}（需要先 capture）"),
    };

    /// <summary>标量的「原样文本」：字符串就是它自己，其余走 JSON 文本（与 Dart 的 `toString()` 同口径）。</summary>
    private static string ScalarText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node?.ToJsonString() ?? "null";

    private static string Describe(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static JsonNode? ParseJson(string text)
    {
        if (text.Trim().Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RejectUnknownKeys(JsonObject spec, string where, string what, string[] known)
    {
        foreach (var key in spec.Select(pair => pair.Key))
        {
            if (Array.IndexOf(known, key) < 0)
            {
                throw Error($"{where} 用了回放器还不认识的 {what} 字段：{key}");
            }
        }
    }

    private static JsonObject RequireObject(JsonNode? node, string where, string what) =>
        node as JsonObject ?? throw Error($"{where} 的 {what} 必须是 JSON 对象");

    private static string RequireString(JsonObject obj, string key, string where) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw Error($"{where} 的 {key} 必须是字符串");

    private static string? OptionalString(JsonObject obj, string key, string where) =>
        obj[key] is null ? null : RequireString(obj, key, where);

    private static int? OptionalInt(JsonObject obj, string key, string where)
    {
        if (obj[key] is null)
        {
            return null;
        }

        return RequireInt(obj[key], where, key);
    }

    private static int RequireInt(JsonNode? node, string where, string what)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var number))
        {
            return number;
        }

        throw Error($"{where} 的 {what} 必须是整数");
    }

    private static XunitException Error(string message) => new(message);

    /// <summary>一次 HTTP 响应的回放器视图（`Raw` 是响应体原始字节，`body_sha256` 用）。</summary>
    private sealed record ReplayResponse(int Status, JsonNode? Body, byte[] Raw, string Text);
}
