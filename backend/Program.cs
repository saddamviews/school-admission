using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var cs = builder.Configuration["DB_CONNECTION"]
         ?? "Host=localhost;Port=5433;Database=admission;Username=postgres;Password=postgres;Maximum Pool Size=40;Timeout=30";
var holdSeconds = int.TryParse(builder.Configuration["HOLD_DURATION_SECONDS"], out var hs) && hs > 0 ? hs : 600;

builder.Services.AddSingleton(NpgsqlDataSource.Create(cs));
builder.Services.AddSingleton(new Settings(holdSeconds));
builder.Services.AddSingleton<SchemaGate>();
builder.Services.AddHostedService<ExpiryService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("X-Server-Time")));

var app = builder.Build();
app.UseCors();
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers["X-Server-Time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        return Task.CompletedTask;
    });
    try { await ctx.RequestServices.GetRequiredService<SchemaGate>().EnsureAsync(); }
    catch
    {
        await Err(503, "DB_UNAVAILABLE", "ডাটাবেস এখনো প্রস্তুত নয়। কিছুক্ষণ পরে আবার চেষ্টা করুন.").ExecuteAsync(ctx);
        return;
    }
    await next();
});

static IResult Err(int status, string code, string msg) => Results.Json(new { code, error = msg }, statusCode: status);
static string? Student(HttpRequest r)
{
    var v = r.Headers["X-Student-Id"].ToString().Trim();
    return v.Length == 17 && v.All(char.IsAsciiDigit) ? v : null;
}
static IResult BadStudent() => Err(400, "INVALID_STUDENT_ID", "জন্ম নিবন্ধন নম্বর অবশ্যই ১৭ অঙ্কের সংখ্যা হতে হবে।");
static string NewId(string p) => p + Guid.NewGuid().ToString("N")[..12];

const string ClassSql = @"SELECT c.id, c.name, c.total_seats,
  COUNT(h.id) FILTER (WHERE h.status='HELD' AND h.expires_at > now())::int,
  COUNT(h.id) FILTER (WHERE h.status='CONFIRMED')::int
  FROM classes c LEFT JOIN holds h ON h.class_id = c.id {0}
  GROUP BY c.id ORDER BY c.created_at, c.id";

static object ReadClass(NpgsqlDataReader r)
{
    int total = r.GetInt32(2), held = r.GetInt32(3), conf = r.GetInt32(4);
    return new { id = r.GetString(0), name = r.GetString(1), totalSeats = total, heldSeats = held, confirmedSeats = conf, availableSeats = Math.Max(0, total - held - conf) };
}

app.MapPost("/classes", async (CreateClass? b, NpgsqlDataSource ds) =>
{
    if (b is null || string.IsNullOrWhiteSpace(b.Name) || b.TotalSeats is null or < 1)
        return Err(400, "VALIDATION", "ক্লাসের নাম দিন এবং আসন সংখ্যা ১ বা তার বেশি হতে হবে।");
    var id = NewId("c_");
    var name = b.Name.Trim();
    await ds.Q("INSERT INTO classes(id,name,total_seats) VALUES($1,$2,$3)", id, name, b.TotalSeats.Value).ExecuteNonQueryAsync();
    return Results.Json(new { id, name, totalSeats = b.TotalSeats.Value }, statusCode: 201);
});

app.MapGet("/classes", async (NpgsqlDataSource ds) =>
{
    await using var cmd = ds.Q(string.Format(ClassSql, ""));
    await using var r = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await r.ReadAsync()) list.Add(ReadClass(r));
    return Results.Json(list);
});

app.MapGet("/classes/{classId}", async (string classId, NpgsqlDataSource ds) =>
{
    await using var cmd = ds.Q(string.Format(ClassSql, "WHERE c.id = $1"), classId);
    await using var r = await cmd.ExecuteReaderAsync();
    return await r.ReadAsync() ? Results.Json(ReadClass(r)) : Err(404, "CLASS_NOT_FOUND", "ক্লাসটি পাওয়া যায়নি।");
});

app.MapPost("/classes/{classId}/holds", async (string classId, HttpRequest req, NpgsqlDataSource ds, Settings st) =>
{
    var sid = Student(req);
    if (sid is null) return BadStudent();
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // The class row lock serialises every hold on this class (across processes too).
        var totalObj = await conn.Q(tx, "SELECT total_seats FROM classes WHERE id=$1 FOR UPDATE", classId).ExecuteScalarAsync();
        if (totalObj is null) return Err(404, "CLASS_NOT_FOUND", "ক্লাসটি পাওয়া যায়নি।");
        var total = (int)totalObj;

        await conn.Q(tx, "UPDATE holds SET status='EXPIRED' WHERE id IN (SELECT id FROM holds WHERE class_id=$1 AND status='HELD' AND expires_at <= clock_timestamp() ORDER BY id FOR UPDATE)", classId).ExecuteNonQueryAsync();

        if (await conn.Q(tx, "SELECT 1 FROM holds WHERE class_id=$1 AND student_id=$2 AND status IN ('HELD','CONFIRMED')", classId, sid).ExecuteScalarAsync() is not null)
            return Err(409, "DUPLICATE_APPLICATION", "আপনি এই ক্লাসে ইতিমধ্যে আবেদন করেছেন।");

        var used = (long)(await conn.Q(tx, "SELECT COUNT(*) FROM holds WHERE class_id=$1 AND status IN ('HELD','CONFIRMED')", classId).ExecuteScalarAsync())!;
        if (used >= total) return Err(409, "SEAT_FULL", "দুঃখিত, এই ক্লাসে আর কোনো আসন খালি নেই।");

        var id = NewId("h_");
        var exp = (DateTime)(await conn.Q(tx,
            "INSERT INTO holds(id,class_id,student_id,status,expires_at) VALUES($1,$2,$3,'HELD', clock_timestamp() + make_interval(secs => $4)) RETURNING expires_at",
            id, classId, sid, (double)st.HoldSeconds).ExecuteScalarAsync())!;
        await tx.CommitAsync();
        return Results.Json(new { holdId = id, classId, studentId = sid, status = "HELD", expiresAt = exp }, statusCode: 201);
    }
    catch (PostgresException e) when (e.SqlState == "23505")
    {
        return Err(409, "DUPLICATE_APPLICATION", "আপনি এই ক্লাসে ইতিমধ্যে আবেদন করেছেন।");
    }
});

const string EffStatus = "CASE WHEN h.status='HELD' AND h.expires_at <= now() THEN 'EXPIRED' ELSE h.status END";

app.MapGet("/holds/{holdId}", async (string holdId, NpgsqlDataSource ds) =>
{
    await using var cmd = ds.Q($"SELECT h.id, h.class_id, {EffStatus}, h.expires_at FROM holds h WHERE h.id=$1", holdId);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return Err(404, "HOLD_NOT_FOUND", "আবেদনটি পাওয়া যায়নি।");
    return Results.Json(new { holdId = r.GetString(0), classId = r.GetString(1), status = r.GetString(2), expiresAt = r.GetDateTime(3) });
});

// Returns (status, expired?) of a student's own hold, or null if it doesn't exist / isn't theirs.
static async Task<string?> CurrentStatus(NpgsqlDataSource ds, string holdId, string sid)
{
    var o = await ds.Q($"SELECT {EffStatus} FROM holds h WHERE h.id=$1 AND h.student_id=$2", holdId, sid).ExecuteScalarAsync();
    return o as string;
}

app.MapPost("/holds/{holdId}/confirm", async (string holdId, HttpRequest req, NpgsqlDataSource ds) =>
{
    var sid = Student(req);
    if (sid is null) return BadStudent();
    // Single atomic conditional UPDATE: only a live HELD hold owned by this student can become CONFIRMED.
    var n = await ds.Q("UPDATE holds SET status='CONFIRMED' WHERE id=$1 AND student_id=$2 AND status='HELD' AND expires_at > now()", holdId, sid).ExecuteNonQueryAsync();
    if (n == 1) return Results.Json(new { holdId, status = "CONFIRMED" });
    return await CurrentStatus(ds, holdId, sid) switch
    {
        null => Err(404, "HOLD_NOT_FOUND", "আবেদনটি পাওয়া যায়নি।"),
        "CONFIRMED" => Results.Json(new { holdId, status = "CONFIRMED" }), // idempotent
        "CANCELLED" => Err(409, "HOLD_CANCELLED", "আবেদনটি বাতিল হয়ে গেছে।"),
        _ => Err(410, "HOLD_EXPIRED", "আবেদনের মেয়াদ শেষ হয়ে গেছে। অনুগ্রহ করে নতুন করে আবেদন করুন।")
    };
});

app.MapDelete("/holds/{holdId}", async (string holdId, HttpRequest req, NpgsqlDataSource ds) =>
{
    var sid = Student(req);
    if (sid is null) return BadStudent();
    var n = await ds.Q("UPDATE holds SET status='CANCELLED' WHERE id=$1 AND student_id=$2 AND status='HELD' AND expires_at > now()", holdId, sid).ExecuteNonQueryAsync();
    if (n == 1) return Results.NoContent();
    return await CurrentStatus(ds, holdId, sid) switch
    {
        null => Err(404, "HOLD_NOT_FOUND", "আবেদনটি পাওয়া যায়নি।"),
        "CANCELLED" => Results.NoContent(), // idempotent
        "CONFIRMED" => Err(409, "ALREADY_CONFIRMED", "ভর্তি নিশ্চিত হয়ে গেছে, তাই বাতিল করা যাবে না।"),
        _ => Err(410, "HOLD_EXPIRED", "আবেদনের মেয়াদ আগেই শেষ হয়ে গেছে।")
    };
});

app.MapGet("/me/holds", async (HttpRequest req, NpgsqlDataSource ds) =>
{
    var sid = Student(req);
    if (sid is null) return BadStudent();
    await using var cmd = ds.Q($"SELECT h.id, h.class_id, c.name, {EffStatus}, h.expires_at FROM holds h JOIN classes c ON c.id=h.class_id WHERE h.student_id=$1 ORDER BY h.created_at DESC", sid);
    await using var r = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await r.ReadAsync())
        list.Add(new { holdId = r.GetString(0), classId = r.GetString(1), className = r.GetString(2), status = r.GetString(3), expiresAt = r.GetDateTime(4) });
    return Results.Json(list);
});

app.Run();

record CreateClass(string? Name, int? TotalSeats);
public partial class Program { }
