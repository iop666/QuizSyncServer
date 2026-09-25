using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuizSync.Server.Tests.Conformance;

/// <summary>
/// 向量里的一步（NDJSON 的一行）。
/// </summary>
/// <param name="Number">`step`：1 起、必须连续（装载时校验）。</param>
/// <param name="Title">`title`：给人看的一句话，失败信息里带上它。</param>
/// <param name="Do">`do`：恰好一个操作。</param>
/// <param name="Expect">`expect`：断言；纯动作步骤可以为空。</param>
internal sealed record VectorStep(int Number, string Title, JsonObject Do, JsonObject? Expect);

/// <summary>
/// 一致性向量的**定位与装载**（格式见 `QuizSyncProtocol/conformance/README.md` §2）。
///
/// 这里只做两件事：找到向量目录、把 NDJSON 读成 <see cref="VectorStep"/> 列表并校验
/// `step` 连续。**不做任何断言**（断言在 <see cref="VectorReplay"/> 里）。
/// </summary>
internal static class NdjsonVector
{
    /// <summary>指定向量目录的环境变量（与 Dart 参考回放器的 `QS_VECTORS` 同名同义）。</summary>
    public const string DirectoryVariable = "QS_VECTORS";

    /// <summary>用来辨认「仓库根」的标记文件。</summary>
    public const string RepositoryMarker = "QuizSyncServer.slnx";

    /// <summary>仓库根 → 向量目录的相对路径（两个仓库并列检出时的默认位置）。</summary>
    private static readonly string[] VectorPathFromRoot =
        ["..", "QuizSyncProtocol", "conformance", "vectors"];

    /// <summary>
    /// 向量目录的绝对路径。
    ///
    /// 优先级：环境变量 <see cref="DirectoryVariable"/> → 从 <see cref="AppContext.BaseDirectory"/>
    /// 往上找到含 <see cref="RepositoryMarker"/> 的目录再拼 `..\QuizSyncProtocol\conformance\vectors`。
    /// **找不到就抛**（调用方应当把它变成一条红的测试，而不是静默跳过）。
    /// </summary>
    public static string ResolveDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var root = FindRepositoryRoot()
            ?? throw new InvalidOperationException(
                $"从 {AppContext.BaseDirectory} 逐级往上都找不到 {RepositoryMarker}，无法推导向量目录；" +
                $"请用环境变量 {DirectoryVariable} 直接指向 QuizSyncProtocol/conformance/vectors");

        return Path.GetFullPath(Path.Combine([root, .. VectorPathFromRoot]));
    }

    /// <summary>目录里所有的 `*.ndjson`（按名字排序，保证测试用例顺序稳定）。</summary>
    public static IReadOnlyList<string> ListVectorFiles(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*.ndjson")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>装载一个向量文件；`step` 不连续、行不是 JSON 对象、缺 `do` 都直接抛。</summary>
    public static IReadOnlyList<VectorStep> Load(string file)
    {
        var steps = new List<VectorStep>();
        var lines = File.ReadAllLines(file, Encoding.UTF8);

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(raw);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{file} 第 {i + 1} 行不是合法 JSON：{ex.Message}");
            }

            if (parsed is not JsonObject step)
            {
                throw new InvalidOperationException($"{file} 第 {i + 1} 行不是 JSON 对象");
            }

            var expected = steps.Count + 1;
            var number = Integer(step, "step", file, i + 1);
            if (number != expected)
            {
                throw new InvalidOperationException(
                    $"{file} 第 {i + 1} 行的 step={number}，应为 {expected}（步骤必须连续）");
            }

            if (step["do"] is not JsonObject action)
            {
                throw new InvalidOperationException($"{file} 第 {i + 1} 行（step {number}）缺少 do");
            }

            var title = step["title"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var text)
                ? text
                : string.Empty;

            steps.Add(new VectorStep(number, title, action, step["expect"] as JsonObject));
        }

        return steps;
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepositoryMarker)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static int Integer(JsonObject step, string key, string file, int line)
    {
        if (step[key] is JsonValue value && value.TryGetValue<int>(out var number))
        {
            return number;
        }

        throw new InvalidOperationException($"{file} 第 {line} 行缺少整数 step");
    }
}
