using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

// Needs the Postgres from docker-compose running (host port 5433 by default).
// Override with TEST_DB_CONNECTION. Hold duration is forced to 2s for the expiry test.
public class ApiFactory : WebApplicationFactory<Program>
{
    public ApiFactory()
    {
        Environment.SetEnvironmentVariable("DB_CONNECTION",
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Port=5433;Database=admission;Username=postgres;Password=postgres;Maximum Pool Size=40;Timeout=60");
        Environment.SetEnvironmentVariable("HOLD_DURATION_SECONDS", "2");
    }
}

public class ApiTests : IClassFixture<ApiFactory>
{
    readonly HttpClient _c;
    static readonly Random Rng = new();
    public ApiTests(ApiFactory f) => _c = f.CreateClient();

    static string NewStudent() { lock (Rng) return string.Concat(Enumerable.Range(0, 17).Select(_ => Rng.Next(10))); }

    async Task<string> NewClass(int seats)
    {
        var r = await _c.PostAsJsonAsync("/classes", new { name = "T-" + Guid.NewGuid().ToString("N")[..6], totalSeats = seats });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    Task<HttpResponseMessage> Send(HttpMethod m, string url, string student)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Add("X-Student-Id", student);
        return _c.SendAsync(req);
    }

    async Task<JsonElement> Cls(string id) => await _c.GetFromJsonAsync<JsonElement>($"/classes/{id}");

    [Fact]
    public async Task Concurrent_holds_never_exceed_total_seats()
    {
        var id = await NewClass(10);
        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => Send(HttpMethod.Post, $"/classes/{id}/holds", NewStudent()))));
        Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(90, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var c = await Cls(id);
        Assert.Equal(10, c.GetProperty("heldSeats").GetInt32());
        Assert.Equal(0, c.GetProperty("availableSeats").GetInt32());
    }

    [Fact]
    public async Task Same_student_cannot_hold_same_class_twice_and_cancel_frees_seat()
    {
        var id = await NewClass(5);
        var s = NewStudent();
        var first = await Send(HttpMethod.Post, $"/classes/{id}/holds", s);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/classes/{id}/holds", s)).StatusCode);

        var holdId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("holdId").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/holds/{holdId}", s)).StatusCode);
        Assert.Equal(5, (await Cls(id)).GetProperty("availableSeats").GetInt32());
    }

    [Fact]
    public async Task Confirm_is_idempotent_and_expired_hold_is_rejected_and_frees_seat()
    {
        var id = await NewClass(2);
        var a = NewStudent();
        var h1 = await (await Send(HttpMethod.Post, $"/classes/{id}/holds", a)).Content.ReadFromJsonAsync<JsonElement>();
        var h1Id = h1.GetProperty("holdId").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/holds/{h1Id}/confirm", a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/holds/{h1Id}/confirm", a)).StatusCode);
        Assert.Equal(1, (await Cls(id)).GetProperty("confirmedSeats").GetInt32());

        var b = NewStudent();
        var h2 = await (await Send(HttpMethod.Post, $"/classes/{id}/holds", b)).Content.ReadFromJsonAsync<JsonElement>();
        await Task.Delay(2500);
        Assert.Equal(HttpStatusCode.Gone, (await Send(HttpMethod.Post, $"/holds/{h2.GetProperty("holdId").GetString()}/confirm", b)).StatusCode);
        var c = await Cls(id);
        Assert.Equal(0, c.GetProperty("heldSeats").GetInt32());
        Assert.Equal(1, c.GetProperty("availableSeats").GetInt32());
    }
}
