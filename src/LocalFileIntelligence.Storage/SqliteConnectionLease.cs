using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.Storage;

public sealed class SqliteConnectionLease : IDisposable, IAsyncDisposable
{
    private SemaphoreSlim? _gate;

    internal SqliteConnectionLease(SqliteConnection connection, SemaphoreSlim gate)
    {
        Connection = connection;
        _gate = gate;
    }

    public SqliteConnection Connection { get; }

    public void Dispose()
    {
        SemaphoreSlim? gate = Interlocked.Exchange(ref _gate, null);
        if (gate is null)
        {
            return;
        }

        try
        {
            Connection.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        SemaphoreSlim? gate = Interlocked.Exchange(ref _gate, null);
        if (gate is null)
        {
            return;
        }

        try
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
