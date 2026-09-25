namespace QuizSync.Server.Core.Storage;

/// <summary>
/// v1 库结构（与 Dart 版逐列对齐：列名即同步 op 的字段名）。
///
/// **与 `QuizSyncProtocol/schema/schema-v1.sql` 必须保持一致** —— 那个文件是
/// 三端对齐用的对外 schema，这里是本实现的内嵌副本。改动时两边一起改，
/// `SchemaParityTests` 会把「对外 schema 里出现的表/列」与这里逐项比对。
/// </summary>
public static class V1Schema
{
    public const int SchemaVersion = 1;

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS devices (
            device_id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            platform TEXT NOT NULL,
            token_hash TEXT NULL,
            paired_at INTEGER NOT NULL,
            last_seen_at INTEGER NULL,
            revoked_at INTEGER NULL,
            app_version TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS images (
            hash TEXT NOT NULL PRIMARY KEY,
            size INTEGER NOT NULL,
            mime TEXT NOT NULL,
            width INTEGER NULL,
            height INTEGER NULL,
            local_path TEXT NULL,
            created_at INTEGER NOT NULL,
            uploaded_by TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_images_created ON images (created_at);

        CREATE TABLE IF NOT EXISTS collections (
            collection_id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            updated_by TEXT NOT NULL,
            lamport INTEGER NOT NULL DEFAULT 0,
            field_clocks_json TEXT NULL,
            deleted_at INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
            session_id TEXT NOT NULL PRIMARY KEY,
            image_hash TEXT NOT NULL,
            source_device TEXT NOT NULL,
            status TEXT NOT NULL,
            question_count INTEGER NOT NULL DEFAULT 0,
            cached INTEGER NOT NULL DEFAULT 0,
            collection_id TEXT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            updated_by TEXT NOT NULL,
            lamport INTEGER NOT NULL DEFAULT 0,
            field_clocks_json TEXT NULL,
            deleted_at INTEGER NULL,
            task_id TEXT NULL,
            error_message TEXT NULL,
            latency_ms INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS session_images (
            session_image_id TEXT NOT NULL PRIMARY KEY,
            session_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            image_hash TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            lamport INTEGER NOT NULL DEFAULT 0,
            field_clocks_json TEXT NULL,
            deleted_at INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS questions (
            question_id TEXT NOT NULL PRIMARY KEY,
            session_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            question_no TEXT NULL,
            stem TEXT NOT NULL DEFAULT '',
            material TEXT NOT NULL DEFAULT '',
            type TEXT NOT NULL DEFAULT 'unknown',
            options_json TEXT NULL,
            choice_json TEXT NULL,
            answer_text TEXT NOT NULL DEFAULT '',
            analysis TEXT NOT NULL DEFAULT '',
            confidence REAL NOT NULL DEFAULT 0.5,
            need_review INTEGER NOT NULL DEFAULT 0,
            answer_in_image INTEGER NOT NULL DEFAULT 0,
            incomplete INTEGER NOT NULL DEFAULT 0,
            answer_guessed INTEGER NOT NULL DEFAULT 0,
            warnings_json TEXT NULL,
            analysis_edited INTEGER NOT NULL DEFAULT 0,
            answer_edited INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            updated_by TEXT NOT NULL,
            lamport INTEGER NOT NULL DEFAULT 0,
            field_clocks_json TEXT NULL,
            deleted_at INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS tasks (
            task_id TEXT NOT NULL PRIMARY KEY,
            image_hash TEXT NOT NULL,
            source_device TEXT NOT NULL,
            status TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            error_code TEXT NULL,
            session_id TEXT NULL,
            created_at INTEGER NOT NULL,
            started_at INTEGER NULL,
            finished_at INTEGER NULL,
            payload_json TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS sync_ops (
            op_id TEXT NOT NULL PRIMARY KEY,
            device_id TEXT NOT NULL,
            lamport INTEGER NOT NULL,
            entity TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            op_type TEXT NOT NULL,
            fields_json TEXT NOT NULL,
            created_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_sync_ops_device_lamport ON sync_ops (device_id, lamport);
        CREATE INDEX IF NOT EXISTS idx_sync_ops_lamport ON sync_ops (lamport);

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS peer_state (
            device_id TEXT NOT NULL PRIMARY KEY,
            sent_lamport INTEGER NOT NULL DEFAULT 0,
            acked_lamport INTEGER NOT NULL DEFAULT 0,
            last_seen_at INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS ai_usage (
            id TEXT NOT NULL PRIMARY KEY,
            called_at INTEGER NOT NULL,
            model TEXT NOT NULL DEFAULT '',
            ok INTEGER NOT NULL DEFAULT 1,
            prompt_tokens INTEGER NOT NULL DEFAULT 0,
            completion_tokens INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_usage_called ON ai_usage (called_at);
        """;
}
