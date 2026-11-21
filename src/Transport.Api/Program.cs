using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Transport")
    ?? throw new InvalidOperationException("Configurer ConnectionStrings__Transport.");
await using var database = NpgsqlDataSource.Create(connectionString);
var app = builder.Build();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (PostgresException ex) when (ex.SqlState is "P1001" or "P1002" or "P1003" or "23P01")
    {
        var (status, message) = ex.SqlState switch
        {
            "23P01" => (409, "Ce siège est déjà occupé sur ce tronçon."),
            "P1002" => (409, ex.MessageText),
            "P1003" => (404, ex.MessageText),
            _ => (400, ex.MessageText)
        };
        await Results.Problem(message, statusCode: status).ExecuteAsync(context);
    }
});

app.MapGet("/api/trajets", async (CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("""
        SELECT t.id, f.origin, f.destination, f.label, f.xof, t.departure
        FROM fare f JOIN trip t ON f.trip_id=t.id WHERE t.departed_at IS NULL AND t.departure > clock_timestamp()
        ORDER BY t.departure, f.origin, f.destination
        """);
    await using var rows = await cmd.ExecuteReaderAsync(ct);
    var items = new List<object>();
    while (await rows.ReadAsync(ct))
        items.Add(new { tripId=rows.GetInt32(0), origin=rows.GetInt32(1), destination=rows.GetInt32(2),
            label=rows.GetString(3), xof=rows.GetInt32(4), departure=rows.GetDateTime(5) });
    return Results.Ok(items);
});

app.MapGet("/api/sieges", async (int tripId, int origin, int destination, CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("""
        SELECT s.number, NOT EXISTS (
            SELECT FROM booking b WHERE b.trip_id=s.trip_id AND b.number=s.number
            AND b.origin < @destination AND @origin < b.destination
            AND (b.state='sold' OR (b.state='hold' AND b.expires_at > statement_timestamp()))
        ) AS available FROM seat s JOIN trip t ON t.id=s.trip_id
        JOIN fare f ON f.trip_id=s.trip_id AND f.origin=@origin AND f.destination=@destination
        WHERE s.trip_id=@trip AND t.departed_at IS NULL AND t.departure > statement_timestamp() ORDER BY s.number
        """);
    cmd.Parameters.AddWithValue("trip",tripId);
    cmd.Parameters.AddWithValue("origin",origin);
    cmd.Parameters.AddWithValue("destination",destination);
    await using var rows = await cmd.ExecuteReaderAsync(ct);
    var seats = new List<object>();
    while (await rows.ReadAsync(ct)) seats.Add(new { number=rows.GetInt32(0), available=rows.GetBoolean(1) });
    return Results.Ok(seats);
});

app.MapPost("/api/options", async (HoldRequest request, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Passenger) || request.Passenger.Length > 100)
        return Results.BadRequest(new { detail="Saisir un nom de 1 à 100 caractères." });
    await using var cmd = database.CreateCommand("""
        SELECT id, xof, expires_at FROM hold_seat(@trip,@seat,@origin,@destination,@who)
        """);
    cmd.Parameters.AddWithValue("trip",request.TripId);
    cmd.Parameters.AddWithValue("seat",request.Number);
    cmd.Parameters.AddWithValue("origin",request.Origin);
    cmd.Parameters.AddWithValue("destination",request.Destination);
    cmd.Parameters.AddWithValue("who",request.Passenger);
    await using var rows = await cmd.ExecuteReaderAsync(ct);
    await rows.ReadAsync(ct);
    return Results.Ok(new { id=rows.GetGuid(0), xof=rows.GetInt32(1), expiresAt=rows.GetDateTime(2) });
});

app.MapGet("/api/caisse", async (string agent, CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("SELECT id FROM cash_session WHERE agent=@agent AND closed_at IS NULL");
    cmd.Parameters.AddWithValue("agent", agent);
    return Results.Ok(new { id = await cmd.ExecuteScalarAsync(ct) });
});

app.MapPost("/api/caisse", async (OpenShift input, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Agent) || input.Agent.Length > 150 || input.Opening < 0)
        return Results.BadRequest(new { detail="Fond ou agent invalide." });
    await using var cmd = database.CreateCommand("""
        INSERT INTO cash_session(agent,opening) VALUES(@agent,@opening)
        ON CONFLICT(agent) WHERE closed_at IS NULL DO UPDATE SET agent=excluded.agent
        WHERE cash_session.opening=excluded.opening RETURNING id
        """);
    cmd.Parameters.AddWithValue("agent", input.Agent);
    cmd.Parameters.AddWithValue("opening", input.Opening);
    var id = await cmd.ExecuteScalarAsync(ct);
    return id is null ? Results.Conflict(new {detail="Une caisse est déjà ouverte avec un autre fond."}) : Results.Ok(new {id});
});

app.MapPost("/api/caisse/{id:guid}/cloture", async (Guid id, CountCash input, CancellationToken ct) =>
{
    if (input.Counted < 0) return Results.BadRequest(new { detail="Comptage négatif." });
    await using var cmd = database.CreateCommand("SELECT expected,counted FROM close_shift(@id,@amount)");
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("amount", input.Counted);
    await using var row = await cmd.ExecuteReaderAsync(ct);
    await row.ReadAsync(ct);
    var expected = row.GetInt64(0);
    return Results.Ok(new {expected, counted=input.Counted, difference=input.Counted-expected});
});

app.MapGet("/api/billets/{ticket:guid}", async (Guid ticket, CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("SELECT passenger,number,label,xof,paid_at,token FROM ticket_document WHERE ticket=@ticket");
    cmd.Parameters.AddWithValue("ticket",ticket);
    await using var row = await cmd.ExecuteReaderAsync(ct);
    if (!await row.ReadAsync(ct)) return Results.NotFound();
    return Results.Ok(new {ticket,passenger=row.GetString(0),number=row.GetInt32(1),label=row.GetString(2),
        xof=row.GetInt32(3),paidAt=row.GetDateTime(4),token=row.GetString(5)});
});

app.MapPost("/api/billets/verifier", async (Proof proof, CancellationToken ct) =>
{
    if (proof.Token is null || proof.Token.Length!=101 || !Guid.TryParse(proof.Token[..36],out var ticket))
        return Results.BadRequest();
    await using var cmd=database.CreateCommand("SELECT EXISTS(SELECT FROM ticket_document WHERE ticket=@ticket AND token=@token)");
    cmd.Parameters.AddWithValue("ticket",ticket);
    cmd.Parameters.AddWithValue("token",proof.Token);
    return Results.Ok(new {valid=await cmd.ExecuteScalarAsync(ct)});
});

app.MapPost("/api/options/{id:guid}/especes", async (Guid id, Guid? session, CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("SELECT ticket,xof,paid_at FROM collect_cash(@id,@session)");
    cmd.Parameters.AddWithValue("id",id);
    cmd.Parameters.AddWithValue("session", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)session ?? DBNull.Value);
    await using var rows = await cmd.ExecuteReaderAsync(ct);
    await rows.ReadAsync(ct);
    return Results.Ok(new { ticket=rows.GetGuid(0), xof=rows.GetInt32(1), paidAt=rows.GetDateTime(2) });
});

Boarding.Map(app,database);
Departures.Map(app,database);
Bags.Map(app,database);
Schedule.Map(app,database);
CashReports.Map(app,database);
Freight.Map(app,database);
app.Run();

record HoldRequest(int TripId, int Number, int Origin, int Destination, string? Passenger);

record OpenShift(string Agent, int Opening);
record CountCash(int Counted);
record Proof(string Token);
