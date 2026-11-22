using Npgsql;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var bootstrapAdmin=args.Length>0 && args[0]=="admin-init";
var builder = WebApplication.CreateBuilder(bootstrapAdmin ? Array.Empty<string>() : args);
var connectionString = builder.Configuration.GetConnectionString("Transport")
    ?? throw new InvalidOperationException("Configurer ConnectionStrings__Transport.");
await using var database = NpgsqlDataSource.Create(connectionString);
if(bootstrapAdmin)
{
    if(args.Length is <2 or >3 || (args.Length==3 && args[2]!="--stdin"))
        throw new InvalidOperationException("Usage : admin-init identifiant [--stdin]");
    await AdminAccess.Bootstrap(database,builder.Configuration["Transport:AdminSeal"],args[1],args.Length==3);
    return;
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("marchand-test", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("editeur", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("option-web", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("suivi-colis", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});
var app = builder.Build();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = file =>
    {
        if(file.Context.Request.Path.StartsWithSegments("/administration") || file.Context.Request.Path.StartsWithSegments("/editeur"))
        {
            file.Context.Response.Headers["Content-Security-Policy"]="frame-ancestors 'none'";
            file.Context.Response.Headers["Referrer-Policy"]="no-referrer";
            file.Context.Response.Headers.CacheControl="no-store";
        }
    }
});
app.UseRouting();
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (PostgresException ex) when (ex.SqlState is "P1001" or "P1002" or "P1003" or "P1011" or "23P01")
    {
        var (status, message) = ex.SqlState switch
        {
            "23P01" => (409, "Ce siège est déjà occupé sur ce tronçon."),
            "P1002" => (409, ex.MessageText),
            "P1011" => (403, ex.MessageText),
            "P1003" => (404, ex.MessageText),
            _ => (400, ex.MessageText)
        };
        await Results.Problem(message, statusCode: status).ExecuteAsync(context);
    }
});

await DeskAccess.Configure(app,database);

app.MapGet("/api/trajets", async (HttpContext ctx,CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("""
        SELECT t.id, f.origin, f.destination, f.label, f.xof, t.departure
        FROM fare f JOIN trip t ON f.trip_id=t.id WHERE t.departed_at IS NULL AND t.departure > clock_timestamp()
          AND (@desk IS NULL OR EXISTS(SELECT FROM counter_desk d JOIN agency_origin o USING(agency_id)
              WHERE d.id=@desk AND o.template_id=coalesce(t.template_id,t.id) AND o.position=f.origin))
        ORDER BY t.departure, f.origin, f.destination
        """);
    cmd.Parameters.AddWithValue("desk",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)DeskAccess.Current(ctx)?.Id ?? DBNull.Value);
    await using var rows = await cmd.ExecuteReaderAsync(ct);
    var items = new List<object>();
    while (await rows.ReadAsync(ct))
        items.Add(new { tripId=rows.GetInt32(0), origin=rows.GetInt32(1), destination=rows.GetInt32(2),
            label=rows.GetString(3), xof=rows.GetInt32(4), departure=rows.GetDateTime(5) });
    return Results.Ok(items);
}).WithMetadata(Audience.Public);

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
}).WithMetadata(Audience.Public);

app.MapPost("/api/options", async (HoldRequest request, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Passenger) || request.Passenger.Length > 100)
        return Results.BadRequest(new { detail="Saisir un nom de 1 à 100 caractères." });
    var desk=DeskAccess.Current(ctx);
    await using var cmd = database.CreateCommand(desk is null
        ? "SELECT id,xof,expires_at FROM hold_seat(@trip,@seat,@origin,@destination,@who)"
        : "SELECT id,xof,expires_at FROM staff_hold(@staff,@desk,@trip,@seat,@origin,@destination,@who)");
    if(desk is not null) DeskAccess.Bind(cmd,ctx);
    cmd.Parameters.AddWithValue("trip",request.TripId);
    cmd.Parameters.AddWithValue("seat",request.Number);
    cmd.Parameters.AddWithValue("origin",request.Origin);
    cmd.Parameters.AddWithValue("destination",request.Destination);
    cmd.Parameters.AddWithValue("who",request.Passenger);
    await using var rows = await cmd.ExecuteReaderAsync(ct);
    await rows.ReadAsync(ct);
    return Results.Ok(new { id=rows.GetGuid(0), xof=rows.GetInt32(1), expiresAt=rows.GetDateTime(2) });
}).WithMetadata(Audience.Counter);

app.MapGet("/api/caisse", async (string agent, HttpContext ctx, CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("SELECT id FROM cash_session WHERE agent=@agent AND desk_id IS NOT DISTINCT FROM @desk AND closed_at IS NULL");
    var desk=DeskAccess.Current(ctx);
    cmd.Parameters.AddWithValue("desk",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)desk?.Id ?? DBNull.Value);
    cmd.Parameters.AddWithValue("agent", desk?.CashAgent ?? agent);
    return Results.Ok(new { id = await cmd.ExecuteScalarAsync(ct) });
}).WithMetadata(Audience.Cash);

app.MapPost("/api/caisse", async (OpenShift input, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Agent) || input.Agent.Length > 150 || input.Opening < 0)
        return Results.BadRequest(new { detail="Fond ou agent invalide." });
    await using var cmd = database.CreateCommand("""
        INSERT INTO cash_session(agent,opening,desk_id,staff_id) VALUES(@agent,@opening,@desk,@staff)
        ON CONFLICT(agent) WHERE closed_at IS NULL DO UPDATE SET agent=excluded.agent
        WHERE cash_session.opening=excluded.opening AND cash_session.desk_id IS NOT DISTINCT FROM excluded.desk_id RETURNING id
        """);
    var desk=DeskAccess.Current(ctx);
    cmd.Parameters.AddWithValue("desk",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)desk?.Id ?? DBNull.Value);
    cmd.Parameters.AddWithValue("staff",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)desk?.StaffId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("agent", desk?.CashAgent ?? input.Agent);
    cmd.Parameters.AddWithValue("opening", input.Opening);
    var id = await cmd.ExecuteScalarAsync(ct);
    return id is null ? Results.Conflict(new {detail="Une caisse est déjà ouverte avec un autre fond."}) : Results.Ok(new {id});
}).WithMetadata(Audience.Cash);

app.MapPost("/api/caisse/{id:guid}/cloture", async (Guid id, CountCash input, HttpContext ctx, CancellationToken ct) =>
{
    if (input.Counted < 0) return Results.BadRequest(new { detail="Comptage négatif." });
    var desk=DeskAccess.Current(ctx);
    await using var cmd = database.CreateCommand(desk is null ? "SELECT expected,counted FROM close_shift(@id,@amount)"
        : "SELECT expected,counted FROM close_shift(own_shift(@id,@desk,@staff),@amount)");
    if(desk is not null) DeskAccess.Bind(cmd,ctx);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("amount", input.Counted);
    await using var row = await cmd.ExecuteReaderAsync(ct);
    await row.ReadAsync(ct);
    var expected = row.GetInt64(0);
    return Results.Ok(new {expected, counted=input.Counted, difference=input.Counted-expected});
}).WithMetadata(Audience.Cash);

app.MapGet("/api/billets/{ticket:guid}", async (Guid ticket, HttpContext ctx, CancellationToken ct) =>
{
    await using var cmd = database.CreateCommand("SELECT passenger,number,label,xof,paid_at,token FROM ticket_document t WHERE ticket=@ticket AND (@desk IS NULL OR EXISTS(SELECT FROM cash_entry e JOIN cash_session s ON s.id=e.session_id WHERE e.ticket=t.ticket AND s.desk_id=@desk AND s.staff_id=@staff))");
    cmd.Parameters.AddWithValue("desk",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)DeskAccess.Current(ctx)?.Id ?? DBNull.Value);
    cmd.Parameters.AddWithValue("staff",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)DeskAccess.Current(ctx)?.StaffId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("ticket",ticket);
    await using var row = await cmd.ExecuteReaderAsync(ct);
    if (!await row.ReadAsync(ct)) return Results.NotFound();
    return Results.Ok(new {ticket,passenger=row.GetString(0),number=row.GetInt32(1),label=row.GetString(2),
        xof=row.GetInt32(3),paidAt=row.GetDateTime(4),token=row.GetString(5)});
}).WithMetadata(Audience.Counter);

app.MapPost("/api/billets/verifier", async (Proof proof, CancellationToken ct) =>
{
    if (proof.Token is null || proof.Token.Length!=101 || !Guid.TryParse(proof.Token[..36],out var ticket))
        return Results.BadRequest();
    await using var cmd=database.CreateCommand("SELECT EXISTS(SELECT FROM ticket_document WHERE ticket=@ticket AND token=@token)");
    cmd.Parameters.AddWithValue("ticket",ticket);
    cmd.Parameters.AddWithValue("token",proof.Token);
    return Results.Ok(new {valid=await cmd.ExecuteScalarAsync(ct)});
});

app.MapPost("/api/options/{id:guid}/especes", async (Guid id, Guid? session, HttpContext ctx, CancellationToken ct) =>
{
    var desk=DeskAccess.Current(ctx);
    await using var cmd = database.CreateCommand(desk is null ? "SELECT ticket,xof,paid_at FROM collect_cash(@id,@session)"
        : "SELECT ticket,xof,paid_at FROM staff_collect(@staff,@desk,@id,@session)");
    if(desk is not null) DeskAccess.Bind(cmd,ctx);
    cmd.Parameters.AddWithValue("id",id);
    cmd.Parameters.AddWithValue("session", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)session ?? DBNull.Value);
    await using var rows = await cmd.ExecuteReaderAsync(ct);
    await rows.ReadAsync(ct);
    return Results.Ok(new { ticket=rows.GetGuid(0), xof=rows.GetInt32(1), paidAt=rows.GetDateTime(2) });
}).WithMetadata(Audience.Counter);

WebReservations.Map(app,database);
Boarding.Map(app,database);
Departures.Map(app,database);
Bags.Map(app,database);
Schedule.Map(app,database);
CashReports.Map(app,database);
Freight.Map(app,database);
FreightDepot.Map(app,database);
FreightTransit.Map(app,database);
FreightSpace.Map(app,database);
app.Run();

record HoldRequest(int TripId, int Number, int Origin, int Destination, string? Passenger);

record OpenShift(string Agent, int Opening);
record CountCash(int Counted);
record Proof(string Token);
