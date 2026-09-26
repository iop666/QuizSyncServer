using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Tasks;

/// <summary>一条识别出来的题目（写进 `questions` 表的最小集合）。</summary>
public sealed record AnalyzedQuestion(
    int Ordinal,
    string Stem,
    string Type,
    string AnswerText,
    string Analysis,
    double Confidence,
    string? QuestionNo = null,
    string Material = "");

/// <summary>识别结果。</summary>
public sealed record AnalyzeOutcome(IReadOnlyList<AnalyzedQuestion> Questions, bool FromCache = false);

/// <summary>
/// 识别能力的抽象。**Server 自己不做 AI**（2.0 架构里 AI 归 Windows 客户端的 Provider 角色），
/// 但 v1 兼容层必须有「提交任务 → 出结果」这条链路，否则老客户端的搜题流程断掉。
/// 所以这里留一个接口：生产实现是「派发给 Provider」（Phase 2 后续），
/// 测试与向量回放用 <see cref="FixtureAnalyzer"/>。
/// </summary>
public interface IAnalyzer
{
    Task<AnalyzeOutcome> AnalyzeAsync(IReadOnlyList<byte[]> pages, CancellationToken ct);
}

/// <summary>
/// 固定答案的识别器：给测试与一致性向量用（回放要的是**确定性**，不是真 AI）。
/// 默认返回 1 道单选，与向量 `tasks.ndjson` 第 9 步期望的 `question_count: 1` 对齐。
/// </summary>
public sealed class FixtureAnalyzer(int questionCount = 1) : IAnalyzer
{
    private readonly int _questionCount = questionCount;

    public Task<AnalyzeOutcome> AnalyzeAsync(IReadOnlyList<byte[]> pages, CancellationToken ct)
    {
        var questions = new List<AnalyzedQuestion>();
        for (var i = 0; i < _questionCount; i++)
        {
            questions.Add(new AnalyzedQuestion(
                Ordinal: i,
                Stem: $"第 {i + 1} 题（fixture）",
                Type: "single_choice",
                AnswerText: "A",
                Analysis: "fixture 解析",
                Confidence: 0.98));
        }

        return Task.FromResult(new AnalyzeOutcome(questions));
    }
}

/// <summary>`sessions` / `questions` 两张表的写入（识别结果落库）。</summary>
public sealed class SessionRepository(QuizSyncDatabase db)
{
    private readonly QuizSyncDatabase _db = db;

    /// <summary>建会话行（任务提交时就建，`question_count` 先给 0）。</summary>
    public void InsertSession(
        string sessionId, string imageHash, string sourceDevice, string collectionId, long nowMs)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_id, image_hash, source_device, status, question_count, cached,
                                  collection_id, created_at, updated_at, updated_by, lamport)
            VALUES ($id, $hash, $src, 'analyzing', 0, 0, $collection, $at, $at, $src, 0)
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$hash", imageHash);
        cmd.Parameters.AddWithValue("$src", sourceDevice);
        cmd.Parameters.AddWithValue("$collection", collectionId);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.ExecuteNonQuery();
    }

    public void SetSessionDone(string sessionId, int questionCount, bool cached, long nowMs, string updatedBy)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET status = 'done', question_count = $count, cached = $cached,
                                updated_at = $at, updated_by = $by
            WHERE session_id = $id
            """;
        cmd.Parameters.AddWithValue("$count", questionCount);
        cmd.Parameters.AddWithValue("$cached", cached ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.Parameters.AddWithValue("$by", updatedBy);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void SetSessionFailed(string sessionId, string message, long nowMs)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET status = 'failed', error_message = $msg, updated_at = $at WHERE session_id = $id";
        cmd.Parameters.AddWithValue("$msg", message);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>题目的序号从 ordinal 开始顺延（同一个会话里不重号）。</summary>
    public int NextQuestionOrdinal(string sessionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(ordinal), -1) + 1 FROM questions WHERE session_id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void InsertQuestion(string sessionId, string questionId, AnalyzedQuestion question, long nowMs, string updatedBy)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO questions (question_id, session_id, ordinal, question_no, stem, material, type,
                                   answer_text, analysis, confidence, created_at, updated_at, updated_by, lamport)
            VALUES ($qid, $sid, $ordinal, $no, $stem, $material, $type, $answer, $analysis, $confidence,
                    $at, $at, $by, 0)
            """;
        cmd.Parameters.AddWithValue("$qid", questionId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$ordinal", question.Ordinal);
        cmd.Parameters.AddWithValue("$no", (object?)question.QuestionNo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stem", question.Stem);
        cmd.Parameters.AddWithValue("$material", question.Material);
        cmd.Parameters.AddWithValue("$type", question.Type);
        cmd.Parameters.AddWithValue("$answer", question.AnswerText);
        cmd.Parameters.AddWithValue("$analysis", question.Analysis);
        cmd.Parameters.AddWithValue("$confidence", question.Confidence);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.Parameters.AddWithValue("$by", updatedBy);
        cmd.ExecuteNonQuery();
    }

    public void InsertSessionImage(string sessionImageId, string sessionId, int ordinal, string imageHash, long nowMs)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_images (session_image_id, session_id, ordinal, image_hash, created_at, lamport)
            VALUES ($id, $sid, $ordinal, $hash, $at, 0)
            """;
        cmd.Parameters.AddWithValue("$id", sessionImageId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$ordinal", ordinal);
        cmd.Parameters.AddWithValue("$hash", imageHash);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.ExecuteNonQuery();
    }

    /// <summary>会话行原样转 JSON（`GET /tasks/<id>` 的 `session` 字段就是它）。</summary>
    public JsonObject? SessionJson(string sessionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions WHERE session_id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var obj = new JsonObject();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (reader.IsDBNull(i))
            {
                obj[name] = null;
                continue;
            }

            var value = reader.GetValue(i);
            obj[name] = value switch
            {
                long l => JsonValue.Create(l),
                int n => JsonValue.Create((long)n),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString()),
            };
        }

        return obj;
    }

    /// <summary>会话的页序 hash（按 ordinal）。</summary>
    public List<string> PageHashes(string sessionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT image_hash FROM session_images WHERE session_id = $id AND deleted_at IS NULL ORDER BY ordinal";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = cmd.ExecuteReader();
        var hashes = new List<string>();
        while (reader.Read())
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
    }

    /// <summary>找「同图 + 同页序 + 已完成」的会话（结果复用；页序必须逐位相同）。</summary>
    public string? FindReusableSession(string imageHash, IReadOnlyList<string> hashes, int window = 500)
    {
        var candidates = new List<string>();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id FROM sessions
                WHERE image_hash = $hash AND status = 'done' AND deleted_at IS NULL
                ORDER BY created_at DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$hash", imageHash);
            cmd.Parameters.AddWithValue("$limit", window);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(reader.GetString(0));
            }
        }

        foreach (var candidate in candidates)
        {
            var pages = PageHashes(candidate);
            if (pages.Count == hashes.Count && pages.SequenceEqual(hashes, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }
}
