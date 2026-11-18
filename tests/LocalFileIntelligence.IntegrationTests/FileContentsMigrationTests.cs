using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.IntegrationTests;

public sealed class FileContentsMigrationTests
{
    [Fact]
    public void FtsMatchesStayCurrentAcrossInsertUpdateAndDelete()
    {
        using SqliteConnection connection = CreateMigratedDatabase();

        InsertContent(connection, "hello local world");
        Assert.Equal(1, Matches(connection, "hello"));

        Execute(connection, "UPDATE file_contents SET extracted_text = 'quiet local world' WHERE file_id = 1;");
        Assert.Equal(0, Matches(connection, "hello"));
        Assert.Equal(1, Matches(connection, "quiet"));

        Execute(connection, "DELETE FROM file_contents WHERE file_id = 1;");
        Assert.Equal(0, Matches(connection, "quiet"));
    }

    [Theory]
    [InlineData("UPDATE file_entries SET revision = 2, extraction_status = 'pending' WHERE id = 1;")]
    [InlineData("UPDATE file_entries SET indexing_status = 'inaccessible' WHERE id = 1;")]
    [InlineData("UPDATE file_entries SET is_deleted = 1 WHERE id = 1;")]
    [InlineData("UPDATE index_roots SET status = 'offline' WHERE id = 1;")]
    [InlineData("UPDATE index_roots SET status = 'access_denied' WHERE id = 1;")]
    [InlineData("UPDATE index_roots SET enabled = 0 WHERE id = 1;")]
    public void RevisionOrAccessChangesInvalidateFtsMatches(string change)
    {
        using SqliteConnection connection = CreateMigratedDatabase();
        InsertContent(connection, "hello local world");

        Execute(connection, change);

        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM file_contents;"));
        Assert.Equal(0, Matches(connection, "hello"));
    }

    [Fact]
    public void RemovingRootRemovesItsFtsMatches()
    {
        using SqliteConnection connection = CreateMigratedDatabase();
        InsertContent(connection, "hello local world");

        Execute(connection, "DELETE FROM index_roots WHERE id = 1;");

        Assert.Equal(0, Matches(connection, "hello"));
    }

    [Fact]
    public void RemovingFileEntryRemovesItsFtsMatches()
    {
        using SqliteConnection connection = CreateMigratedDatabase();
        InsertContent(connection, "hello local world");

        Execute(connection, "DELETE FROM file_entries WHERE id = 1;");

        Assert.Equal(0, Matches(connection, "hello"));
    }

    [Fact]
    public void FtsTokenizerMatchesWithoutDiacritics()
    {
        using SqliteConnection connection = CreateMigratedDatabase();
        InsertContent(connection, "café local world");

        Assert.Equal(1, Matches(connection, "cafe"));
    }

    [Fact]
    public void ContentRequiresTheCurrentIndexedFileRevision()
    {
        using SqliteConnection connection = CreateMigratedDatabase();
        Execute(connection, "UPDATE file_entries SET revision = 2 WHERE id = 1;");

        SqliteException exception = Assert.Throws<SqliteException>(() => InsertContent(connection, "hello"));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    private static SqliteConnection CreateMigratedDatabase()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        Execute(connection,
            """
            PRAGMA foreign_keys = ON;
            CREATE TABLE index_roots (
                id INTEGER PRIMARY KEY,
                enabled INTEGER NOT NULL,
                status TEXT NOT NULL,
                updated_at TEXT NOT NULL
            ) STRICT;
            CREATE TABLE file_entries (
                id INTEGER PRIMARY KEY,
                root_id INTEGER NOT NULL REFERENCES index_roots(id) ON DELETE CASCADE,
                revision INTEGER NOT NULL,
                entry_type TEXT NOT NULL,
                is_deleted INTEGER NOT NULL,
                indexing_status TEXT NOT NULL,
                extraction_status TEXT NOT NULL,
                UNIQUE (id, revision)
            ) STRICT;
            INSERT INTO index_roots VALUES (1, 1, 'ready', '2026-01-01T00:00:00.000Z');
            INSERT INTO file_entries VALUES (1, 1, 1, 'file', 0, 'indexed', 'extracted');
            """);
        Execute(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lfi015FileContentsFts.sql")));
        return connection;
    }

    private static void InsertContent(SqliteConnection connection, string text)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO file_contents
            VALUES (1, 1, $text, 'plain-text', '1', '2026-01-01T00:00:00.000Z', length($text));
            """;
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
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
