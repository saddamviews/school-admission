using Npgsql;

public record Settings(int HoldSeconds);

public static class Db
{
    public static NpgsqlCommand Q(this NpgsqlDataSource ds, string sql, params object[] ps)
    {
        var cmd = ds.CreateCommand(sql);
        foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p });
        return cmd;
    }

    public static NpgsqlCommand Q(this NpgsqlConnection c, NpgsqlTransaction t, string sql, params object[] ps)
    {
        var cmd = new NpgsqlCommand(sql, c, t);
        foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p });
        return cmd;
    }

    const string Schema = @"
CREATE TABLE IF NOT EXISTS classes(
  id text PRIMARY KEY,
  name text NOT NULL,
  total_seats int NOT NULL CHECK (total_seats > 0),
  created_at timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE TABLE IF NOT EXISTS holds(
  id text PRIMARY KEY,
  class_id text NOT NULL REFERENCES classes(id),
  student_id text NOT NULL,
  status text NOT NULL,
  expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE UNIQUE INDEX IF NOT EXISTS uq_active_hold ON holds(class_id, student_id) WHERE status IN ('HELD','CONFIRMED');
CREATE INDEX IF NOT EXISTS ix_holds_student ON holds(student_id);
CREATE INDEX IF NOT EXISTS ix_holds_class_status ON holds(class_id, status);";

    // Advisory lock: two backend processes starting together won't race on CREATE TABLE.
    public static async Task InitAsync(NpgsqlDataSource ds)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await ds.OpenConnectionAsync();
                await using (var l = new NpgsqlCommand("SELECT pg_advisory_lock(727001)", conn)) await l.ExecuteNonQueryAsync();
                try { await using var s = new NpgsqlCommand(Schema, conn); await s.ExecuteNonQueryAsync(); }
                finally { await using var u = new NpgsqlCommand("SELECT pg_advisory_unlock(727001)", conn); await u.ExecuteNonQueryAsync(); }
                return;
            }
            catch when (attempt < 15) { await Task.Delay(1000); }
        }
    }
}

public class SchemaGate(NpgsqlDataSource ds)
{
    bool _ok;
    readonly SemaphoreSlim _l = new(1, 1);
    public async Task EnsureAsync()
    {
        if (_ok) return;
        await _l.WaitAsync();
        try { if (_ok) return; await Db.InitAsync(ds); _ok = true; }
        finally { _l.Release(); }
    }
}

// Safety net: flips old HELD rows to EXPIRED. Correctness does not depend on it
// (every count/read also checks expires_at > now()).
public class ExpiryService(NpgsqlDataSource ds, SchemaGate gate, ILogger<ExpiryService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await gate.EnsureAsync();
                await ds.Q("UPDATE holds SET status='EXPIRED' WHERE id IN (SELECT id FROM holds WHERE status='HELD' AND expires_at <= now() ORDER BY id FOR UPDATE)").ExecuteNonQueryAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { log.LogWarning(e, "expiry sweep failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
        }
    }
}
