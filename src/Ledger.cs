using Microsoft.Data.Sqlite;

namespace Rehearsal;

sealed record Fire(string Id, string Scheduled, string Fired, string FireInstance, string Worker);
sealed class Ledger(string path)
{
    internal readonly string Connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    SqliteConnection Open()
    {
        var db = new SqliteConnection(Connection);
        db.Open();
        return db;
    }

    static SqliteCommand Command(SqliteConnection db, string sql, params object[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"@p{i}", values[i]);
        return command;
    }

    public void Create(Manifest manifest)
    {
        using var db = Open();
        using var command = Command(db, """
            CREATE TABLE input (id INTEGER PRIMARY KEY CHECK(id = 1), manifest TEXT NOT NULL, revision INTEGER NOT NULL, ready INTEGER NOT NULL, approved TEXT);
            CREATE TABLE attempt (fire_instance TEXT PRIMARY KEY, id TEXT NOT NULL, scheduled TEXT NOT NULL, fired TEXT NOT NULL, worker TEXT NOT NULL);
            CREATE TABLE effect (id TEXT PRIMARY KEY, scheduled TEXT NOT NULL, fired TEXT NOT NULL, fire_instance TEXT NOT NULL, worker TEXT NOT NULL);
            CREATE TRIGGER effect_no_update BEFORE UPDATE ON effect BEGIN SELECT RAISE(ABORT,'history is immutable'); END;
            CREATE TRIGGER effect_no_delete BEFORE DELETE ON effect BEGIN SELECT RAISE(ABORT,'history is immutable'); END;
            INSERT INTO input VALUES (1,@p0,1,0,NULL);
            """, Format.Write(manifest));
        command.ExecuteNonQuery();
    }

    public (Manifest Manifest, long Revision, bool Ready, Plan? Approved) State()
    {
        using var db = Open();
        using var command = Command(db, "SELECT manifest,revision,ready,approved FROM input WHERE id=1");
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("Missing scenario.");
        return (Format.Read<Manifest>(row.GetString(0)), row.GetInt64(1), row.GetBoolean(2),
            row.IsDBNull(3) ? null : Format.Read<Plan>(row.GetString(3)));
    }

    public void Change(string sql, params object[] values)
    {
        using var db = Open();
        using var command = Command(db, sql, values);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The scenario changed or approval is already sealed.");
    }

    public void Record(Fire fire)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        // An invocation and its local effect commit together. Keep every distinct callback observable.
        using var command = Command(db, """
            INSERT INTO attempt VALUES (@p0,@p1,@p2,@p3,@p4);
            INSERT INTO effect VALUES (@p1,@p2,@p3,@p0,@p4) ON CONFLICT(id) DO NOTHING;
            """, fire.FireInstance, fire.Id, fire.Scheduled, fire.Fired, fire.Worker);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public Fire[] Fires(bool attempts = false)
    {
        using var db = Open();
        var table = attempts ? "attempt" : "effect";
        using var command = Command(db, $"SELECT id,scheduled,fired,fire_instance,worker FROM {table} ORDER BY scheduled,id,fire_instance");
        using var rows = command.ExecuteReader();
        var result = new List<Fire>();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4)));
        return result.ToArray();
    }
}
