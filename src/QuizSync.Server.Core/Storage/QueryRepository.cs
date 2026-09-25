using System.Text.Json.Nodes;

namespace QuizSync.Server.Core.Storage;

/// <summary>合集：列表、存在性、当前选中（`settings.active_collection_id`）。</summary>
public sealed class CollectionRepository(QuizSyncDatabase db)
{
    public const string ActiveKey = "active_collection_id";

    private readonly QuizSyncDatabase _db = db;

    public bool Exists(string collectionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM collections WHERE collection_id = $id AND deleted_at IS NULL LIMIT 1";
        cmd.Parameters.AddWithValue("$id", collectionId);
        return cmd.ExecuteScalar() is not null;
    }

    public string? ActiveId()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", ActiveKey);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    public void SetActive(string collectionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", ActiveKey);
        cmd.Parameters.AddWithValue("$v", collectionId);
        cmd.ExecuteNonQuery();
    }

    public JsonArray List()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT collection_id, name, created_at, updated_at, updated_by, lamport, deleted_at
            FROM collections WHERE deleted_at IS NULL ORDER BY created_at LIMIT 500
            """;
        using var reader = cmd.ExecuteReader();
        var list = new JsonArray();
        while (reader.Read())
        {
            list.Add(new JsonObject
            {
                ["collection_id"] = reader.GetString(0),
                ["name"] = reader.GetString(1),
                ["created_at"] = reader.GetInt64(2),
                ["updated_at"] = reader.GetInt64(3),
                ["updated_by"] = reader.GetString(4),
                ["lamport"] = reader.GetInt64(5),
                ["deleted_at"] = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            });
        }

        return list;
    }
}

/// <summary>存在性查询（给 404/409 分支用；完整实体读取在各自的仓储里）。</summary>
public sealed class ExistenceRepository(QuizSyncDatabase db)
{
    private readonly QuizSyncDatabase _db = db;

    public bool SessionExists(string sessionId) => RowExists("sessions", "session_id", sessionId);

    public bool TaskExists(string taskId) => RowExists("tasks", "task_id", taskId);

    /// <summary>该会话拥有的图片 hash（页序行优先；没有页序行时回落到会话自己的 `image_hash`）。</summary>
    public List<string> ImageHashesOf(string sessionId)
    {
        var hashes = new List<string>();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT image_hash FROM session_images
                WHERE session_id = $id AND deleted_at IS NULL ORDER BY ordinal
                """;
            cmd.Parameters.AddWithValue("$id", sessionId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hashes.Add(reader.GetString(0));
            }
        }

        if (hashes.Count == 0)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT image_hash FROM sessions WHERE session_id = $id";
            cmd.Parameters.AddWithValue("$id", sessionId);
            var value = cmd.ExecuteScalar();
            if (value is string s && s.Length > 0)
            {
                hashes.Add(s);
            }
        }

        return hashes;
    }

    public bool ImageExists(string hash)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM images WHERE hash = $h LIMIT 1";
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() is not null;
    }

    private bool RowExists(string table, string idColumn, string id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {table} WHERE {idColumn} = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }
}
