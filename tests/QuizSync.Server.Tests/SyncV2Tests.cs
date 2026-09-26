using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QuizSync.Server.Core;

namespace QuizSync.Server.Tests;

/// <summary>
/// v2 的同步拉取：`since_lamport` 与 `cursor` **收敛为单一游标参数**（spec/04 第 6 条）。
/// 老参数**明确报错**而不是静默忽略 —— 否则客户端会以为自己在用 v1 语义。
/// </summary>
public sealed class SyncV2Tests
{
    private const string ControlToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static async Task<(QuizSyncHost Host, HttpClient Client, string Token)> StartPairedAsync()
    {
        var host = await QuizSyncHost.StartAsync(new ServerOptions
        {
            PreferredPort = 0,
            ServerName = "V2-TEST",
            AppVersion = "2.0.0",
            ControlToken = ControlToken,
        });

        var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        client.DefaultRequestHeaders.Add("X-QS-Control", ControlToken);
        var code = (await client.GetFromJsonAsync<JsonElement>("/api/v1/pair/code")).GetProperty("code").GetString()!;
        client.DefaultRequestHeaders.Remove("X-QS-Control");

        var pair = await client.PostAsJsonAsync("/api/v1/pair", new
        {
            code,
            device_id = "dev-v2",
            device_name = "v2 测试机",
            platform = "windows",
            app_version = "2.0.0",
        });
        var token = (await pair.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (host, client, token);
    }

    [Fact]
    public async Task Since_lamport_is_rejected_on_v2_with_a_clear_message()
    {
        var (host, client, _) = await StartPairedAsync();
        await using (host)
        using (client)
        {
            var resp = await client.GetAsync("/api/v2/sync/ops?from_device=dev-v2&since_lamport=5");

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_request", json.GetProperty("code").GetString());
            Assert.Contains("cursor", json.GetProperty("message").GetString()!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task From_device_is_required()
    {
        var (host, client, _) = await StartPairedAsync();
        await using (host)
        using (client)
        {
            var resp = await client.GetAsync("/api/v2/sync/ops");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Cursor_is_exclusive_and_paging_reports_the_next_cursor()
    {
        var (host, client, _) = await StartPairedAsync();
        await using (host)
        using (client)
        {
            // 空页：ops 为空、has_more=false、next_cursor=null。
            var emptyResp = await client.GetAsync("/api/v2/sync/ops?from_device=dev-v2&cursor=0");
            var emptyText = await emptyResp.Content.ReadAsStringAsync();
            Assert.True(emptyResp.IsSuccessStatusCode, $"空页拉取失败：{(int)emptyResp.StatusCode} {emptyText}");
            var empty = JsonDocument.Parse(emptyText).RootElement;
            Assert.Empty(empty.GetProperty("ops").EnumerateArray());
            Assert.False(empty.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("next_cursor").ValueKind);

            // 推两条 op（device_id 必须与调用者一致，否则会被计成 rejected）。
            // 注意 fields_json 要给齐该实体「NOT NULL 且无默认值」的列：collections 是
            // name / created_at / updated_at / updated_by —— 少一个就得 400（不是 500）。
            var body = """
                {"ops":[
                  {"op_id":"op-v2-1","device_id":"dev-v2","lamport":1,"entity":"collection","entity_id":"c-1","op_type":"upsert","fields_json":{"name":"合集一","created_at":1700000000000,"updated_at":1700000000000,"updated_by":"dev-v2"}},
                  {"op_id":"op-v2-2","device_id":"dev-v2","lamport":2,"entity":"collection","entity_id":"c-2","op_type":"upsert","fields_json":{"name":"合集二","created_at":1700000000000,"updated_at":1700000000000,"updated_by":"dev-v2"}}
                ]}
                """;
            var push = await client.PostAsync("/api/v1/sync/ops", new StringContent(body, Encoding.UTF8, "application/json"));
            var pushText = await push.Content.ReadAsStringAsync();
            Assert.True(push.IsSuccessStatusCode, $"推 op 失败：{(int)push.StatusCode} {pushText}");
            var pushed = JsonDocument.Parse(pushText).RootElement;
            Assert.Equal(2, pushed.GetProperty("applied").GetInt32());

            // 从 0 拉：两条都回来，next_cursor = 最后一条的 lamport。
            var page = await client.GetFromJsonAsync<JsonElement>("/api/v2/sync/ops?from_device=dev-v2&cursor=0");
            Assert.Equal(2, page.GetProperty("ops").GetArrayLength());
            var next = page.GetProperty("next_cursor").GetInt64();
            Assert.Equal(2, next);

            // 用 next_cursor 再拉：游标是**排他**的 → 一条都不该重复回来。
            var after = await client.GetFromJsonAsync<JsonElement>($"/api/v2/sync/ops?from_device=dev-v2&cursor={next}");
            Assert.Empty(after.GetProperty("ops").EnumerateArray());
            Assert.False(after.GetProperty("has_more").GetBoolean());
        }
    }

    [Fact]
    public async Task Unauthenticated_v2_pull_is_rejected()
    {
        var (host, client, _) = await StartPairedAsync();
        await using (host)
        using (client)
        {
            using var anonymous = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
            var resp = await anonymous.GetAsync("/api/v2/sync/ops?from_device=dev-v2&cursor=0");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Op_missing_required_columns_is_a_protocol_error_not_a_500()
    {
        var (host, client, _) = await StartPairedAsync();
        await using (host)
        using (client)
        {
            // 缺 session_id / ordinal / type（questions 里「NOT NULL 且无默认值」的列）→ 必须 400 + 原因，
            // 而不是未捕获异常导致的 500。
            var body = """
                {"ops":[{"op_id":"op-bad","device_id":"dev-v2","lamport":9,"entity":"question","entity_id":"q-bad","op_type":"upsert","fields_json":{"stem":"缺列"}}]}
                """;
            var resp = await client.PostAsync("/api/v1/sync/ops", new StringContent(body, Encoding.UTF8, "application/json"));
            var text = await resp.Content.ReadAsStringAsync();

            // 协议层面只要求两件事：**不是 500**（不留未捕获异常），且要么明确报错（400）、
            // 要么按 op 级裁决计成 rejected（向量里对「不该接受的 op」就是这么记的）。
            Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
            var handled = resp.StatusCode == HttpStatusCode.BadRequest
                || (resp.IsSuccessStatusCode && text.Contains("\"rejected\":1", StringComparison.Ordinal));
            Assert.True(handled, $"既不是 400 也没计成 rejected：{(int)resp.StatusCode} {text}");
        }
    }
}
