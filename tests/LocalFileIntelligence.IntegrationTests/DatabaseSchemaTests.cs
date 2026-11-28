using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.IntegrationTests;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public void RemovingRootPurgesContentFtsWorkAndGrants()
    {
        using SqliteConnection connection = CreateSeededDatabase();
        Assert.Equal(1, Matches(connection, "hello"));

        Execute(connection, "DELETE FROM index_roots WHERE id = 1;");

        foreach (string table in new[] { "file_entries", "file_contents", "index_jobs", "client_root_grants" })
        {
            Assert.Equal(0, Scalar(connection, $"SELECT count(*) FROM {table};"));
        }

        Assert.Equal(0, Matches(connection, "hello"));
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Theory]
    [InlineData("UPDATE file_entries SET revision = 2, extraction_status = 'pending' WHERE id = 1;")]
    [InlineData("UPDATE file_entries SET indexing_status = 'inaccessible' WHERE id = 1;")]
    [InlineData("UPDATE file_entries SET is_deleted = 1, deleted_at = '2026-01-01T00:00:01.000Z' WHERE id = 1;")]
    [InlineData("UPDATE file_entries SET extraction_status = 'failed' WHERE id = 1;")]
    [InlineData("UPDATE index_roots SET status = 'access_denied' WHERE id = 1;")]
    [InlineData("UPDATE index_roots SET status = 'offline' WHERE id = 1;")]
    [InlineData("UPDATE index_roots SET enabled = 0 WHERE id = 1;")]
    public void RevisionOrAccessChangeInvalidatesCachedContent(string change)
    {
        using SqliteConnection connection = CreateSeededDatabase();

        Execute(connection, change);

        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM file_contents;"));
        Assert.Equal(0, Matches(connection, "hello"));
    }

    [Fact]
    public void UpdatingExternalContentRemovesOldFtsTerms()
    {
        using SqliteConnection connection = CreateSeededDatabase();

        Execute(connection, "UPDATE file_contents SET extracted_text = 'quiet local world' WHERE file_id = 1;");

        Assert.Equal(0, Matches(connection, "hello"));
        Assert.Equal(1, Matches(connection, "quiet"));
    }

    [Fact]
    public void ContentMustMatchTheCurrentFileRevision()
    {
        using SqliteConnection connection = CreateSeededDatabase();
        Execute(connection, "DELETE FROM file_contents;");

        SqliteException exception = Assert.Throws<SqliteException>(() => Execute(connection,
            "INSERT INTO file_contents VALUES (1, 2, 'hello', 'plain-text', '1', '2026-01-01T00:00:00.000Z', 5);"));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void ContentUpdatesMustTargetACurrentEligibleFile()
    {
        using SqliteConnection connection = CreateSeededDatabase();
        Execute(connection,
            """
            INSERT INTO file_entries (id, root_id, relative_path, path_key, parent_path_key, name, extension,
                                      entry_type, size_bytes, created_at_fs, modified_at_fs, last_seen_at,
                                      extraction_status, indexing_status)
            VALUES
                (2, 1, 'folder', 'FOLDER', '', 'folder', '', 'directory', 0,
                 '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                 'pending', 'pending'),
                (3, 1, 'pending.txt', 'PENDING.TXT', '', 'pending.txt', '.txt', 'file', 0,
                 '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                 'pending', 'pending');
            """);

        foreach (int targetFileId in new[] { 2, 3 })
        {
            SqliteException exception = Assert.Throws<SqliteException>(() => Execute(connection,
                $"UPDATE file_contents SET file_id = {targetFileId}, file_revision = 1 WHERE file_id = 1;"));

            Assert.Equal(19, exception.SqliteErrorCode);
            Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM file_contents WHERE file_id = 1;"));
            Assert.Equal(1, Matches(connection, "hello"));
        }
    }

    [Fact]
    public void PreventsCrossRootFileJobs()
    {
        using SqliteConnection connection = CreateSeededDatabase();
        Execute(connection,
            """
            INSERT INTO index_roots (id, path, path_key, display_name, enabled, status, volume_identity,
                                     directory_identity, created_at, updated_at)
            VALUES (2, 'C:\Synthetic2', 'C:\SYNTHETIC2', 'Synthetic 2', 1, 'ready', 'volume-1', 'directory-2',
                    '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
            """);

        SqliteException exception = Assert.Throws<SqliteException>(() => Execute(connection,
            """
            INSERT INTO index_jobs (file_id, root_id, job_type, dedupe_key, status, created_at, next_attempt_at,
                                    expected_file_revision, expected_policy_revision)
            VALUES (1, 2, 'extract', 'file-1', 'queued', '2026-01-01T00:00:00.000Z',
                    '2026-01-01T00:00:00.000Z', 1, 1);
            """));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void EnforcesWorkDeduplicationAndLeaseState()
    {
        using SqliteConnection connection = CreateSeededDatabase();

        SqliteException duplicate = Assert.Throws<SqliteException>(() => Execute(connection,
            """
            INSERT INTO index_jobs (file_id, root_id, job_type, dedupe_key, status, created_at, next_attempt_at,
                                    expected_file_revision, expected_policy_revision)
            VALUES (1, 1, 'extract', 'file-1', 'queued', '2026-01-01T00:00:00.000Z',
                    '2026-01-01T00:00:00.000Z', 1, 1);
            """));
        SqliteException missingLease = Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE index_jobs SET status = 'running' WHERE id = 1;"));

        Assert.Equal(19, duplicate.SqliteErrorCode);
        Assert.Equal(19, missingLease.SqliteErrorCode);
    }

    [Fact]
    public void ContentGrantsCannotBypassMetadataConsent()
    {
        using SqliteConnection connection = CreateSeededDatabase();

        SqliteException exception = Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE client_root_grants SET allow_metadata = 0, allow_content = 1;"));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void RejectsNonUtcTimestampRepresentation()
    {
        using SqliteConnection connection = CreateSeededDatabase();

        SqliteException exception = Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE index_roots SET updated_at = '2026-01-01T03:00:00+03:00' WHERE id = 1;"));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void PendingOperationsCannotSkipApprovalMetadata()
    {
        using SqliteConnection connection = CreateSeededDatabase();

        SqliteException exception = Assert.Throws<SqliteException>(() => Execute(connection,
            """
            INSERT INTO pending_operations (root_id, operation_type, payload_json, status, requested_at, expires_at)
            VALUES (1, 'future-operation', '{}', 'executing', '2026-01-01T00:00:00.000Z', '2026-01-01T01:00:00.000Z');
            """));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    private static SqliteConnection CreateSeededDatabase()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        Execute(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema.sql")));
        Execute(connection,
            """
            INSERT INTO index_roots (id, path, path_key, display_name, enabled, status, volume_identity,
                                     directory_identity, created_at, updated_at)
            VALUES (1, 'C:\Synthetic', 'C:\SYNTHETIC', 'Synthetic root', 1, 'ready', 'volume-1', 'directory-1',
                    '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
            INSERT INTO file_entries (id, root_id, relative_path, path_key, parent_path_key, name, extension,
                                      entry_type, size_bytes, created_at_fs, modified_at_fs, last_seen_at,
                                      extraction_status, indexing_status)
            VALUES (1, 1, 'notes.txt', 'NOTES.TXT', '', 'notes.txt', '.txt', 'file', 17,
                    '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                    'extracted', 'indexed');
            INSERT INTO file_contents
            VALUES (1, 1, 'hello local world', 'plain-text', '1', '2026-01-01T00:00:00.000Z', 17);
            INSERT INTO mcp_clients (id, profile_id, display_name, capability_sha256, enabled, created_at)
            VALUES (1, '33333333-3333-4333-8333-333333333333', 'Synthetic client', zeroblob(32), 1,
                    '2026-01-01T00:00:00.000Z');
            INSERT INTO client_root_grants VALUES (1, 1, 1, 1, '2026-01-01T00:00:00.000Z');
            INSERT INTO index_jobs (id, file_id, root_id, job_type, dedupe_key, status, created_at, next_attempt_at,
                                    expected_file_revision, expected_policy_revision)
            VALUES (1, 1, 1, 'extract', 'file-1', 'queued', '2026-01-01T00:00:00.000Z',
                    '2026-01-01T00:00:00.000Z', 1, 1);
            """);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long Matches(SqliteConnection connection, string term)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM file_contents_fts WHERE file_contents_fts MATCH $term;";
        command.Parameters.AddWithValue("$term", term);
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
