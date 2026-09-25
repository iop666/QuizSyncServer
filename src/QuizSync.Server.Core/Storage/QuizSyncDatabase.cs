using Microsoft.Data.Sqlite;

namespace QuizSync.Server.Core.Storage;

/// <summary>
/// 本地库（SQLite）。表结构与 <c>data-model.md</c> / <c>schema/schema-v1.sql</c> 一致：
/// **列名就是同步 op 的字段名**（`fields_json` 的键），所以这里不许改名。
///
/// 与 v1 一致的口径：
/// - 时间戳一律 UTC 毫秒整数；
/// - 软删除用可空的 `deleted_at`；
/// - 参与同步的表额外有 `lamport` / `field_clocks_json` / `updated_by`；
/// - **本地专属列不进同步**：`images.local_path`、`devices.token_hash`、
///   `settings.*`、`peer_state.*`、`tasks.*`。
/// </summary>
public sealed class QuizSyncDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    private QuizSyncDatabase(SqliteConnection connection) => _connection = connection;

    /// <summary>打开（或新建）库。`path` 为 <c>:memory:</c> 时是内存库（测试用）。</summary>
    public static QuizSyncDatabase Open(string path)
    {
        // 注意：不能用裸的 `:memory:` + shared cache —— 那样**同一个进程里所有**
        // 内存库都是同一份数据，测试之间会互相污染（实测：前一个用例配过对的设备
        // 会让后一个用例的首次配对变成 409）。给每个内存库一个唯一名字。
        var connectionString = path == ":memory:"
            ? $"Data Source=file:qs{Guid.NewGuid():N}?mode=memory&cache=shared"
            : new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var db = new QuizSyncDatabase(connection);
        db.Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        db.CreateSchema();
        return db;
    }

    public SqliteConnection Connection => _connection;

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema() => Execute(V1Schema.Ddl);

    public void Dispose() => _connection.Dispose();
}
