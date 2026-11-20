using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Transport")
    ?? throw new InvalidOperationException("Configurer ConnectionStrings__Transport.");
await using var database = NpgsqlDataSource.Create(connectionString);
var app = builder.Build();

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
        FROM fare f JOIN trip t ON f.trip_id=t.id WHERE t.departure > clock_timestamp()
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
        WHERE s.trip_id=@trip AND t.departure > statement_timestamp() ORDER BY s.number
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

app.MapPost("/api/options/{id:guid}/especes", async (Guid id, CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("SELECT ticket,xof,paid_at FROM confirm_cash(@id)");
    cmd.Parameters.AddWithValue("id",id);
    await using var rows = await cmd.ExecuteReaderAsync(ct);
    await rows.ReadAsync(ct);
    return Results.Ok(new { ticket=rows.GetGuid(0), xof=rows.GetInt32(1), paidAt=rows.GetDateTime(2) });
});

app.Run();

record HoldRequest(int TripId, int Number, int Origin, int Destination, string? Passenger);
