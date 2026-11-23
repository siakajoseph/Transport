using Npgsql;
using QRCoder;

internal static class WebReservations
{
    private sealed record HoldLookup(Guid Reference);

    internal static void Map(WebApplication app, NpgsqlDataSource database)
    {
        var sandbox = app.Configuration.GetValue<bool>("Transport:SandboxPayments");
        if (sandbox) MapSandbox(app, database);
        app.MapGet("/api/reservations/disponibilite", async (CancellationToken ct) =>
        {
            await using var cmd = database.CreateCommand("SELECT enabled FROM web_subscription");
            return Results.Ok(new { open = await cmd.ExecuteScalarAsync(ct) is true });
        }).WithMetadata(Audience.Public);

        app.MapPost("/api/reservations/reprise",async(HoldLookup input,HttpContext ctx,CancellationToken ct)=>{
            ctx.Response.Headers.CacheControl="no-store";
            await using var cmd=database.CreateCommand("""
                SELECT b.number,b.xof,b.expires_at,c.name,clock_timestamp()
                FROM web_order w JOIN booking b ON b.id=w.booking_id
                JOIN trip t ON t.id=b.trip_id CROSS JOIN company c
                WHERE w.reference=@ref AND b.state='hold' AND t.departed_at IS NULL
                  AND b.expires_at>clock_timestamp()
                """);
            cmd.Parameters.AddWithValue("ref",input.Reference);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)) return Results.NotFound();
            return Results.Ok(new {input.Reference,number=row.GetInt32(0),xof=row.GetInt32(1),
                expiresAt=row.GetDateTime(2),company=row.GetString(3),serverTime=row.GetDateTime(4),sandbox});
        }).WithMetadata(Audience.Public).RequireRateLimiting("reprise-web");

        app.MapPost("/api/reservations", async (WebRequest input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input.Reference == Guid.Empty || string.IsNullOrWhiteSpace(input.Passenger)
                || input.Passenger.Length > 100)
                return Results.BadRequest(new { detail = "Référence ou nom du passager invalide." });

            await using var cmd = database.CreateCommand("""
                SELECT expires_at, xof, c.name,clock_timestamp() FROM hold_web(@ref,@trip,@seat,@origin,@destination,@who)
                CROSS JOIN company c
                """);
            cmd.Parameters.AddWithValue("ref", input.Reference);
            cmd.Parameters.AddWithValue("trip", input.TripId);
            cmd.Parameters.AddWithValue("seat", input.Number);
            cmd.Parameters.AddWithValue("origin", input.Origin);
            cmd.Parameters.AddWithValue("destination", input.Destination);
            cmd.Parameters.AddWithValue("who", input.Passenger.Trim());
            await using var row = await cmd.ExecuteReaderAsync(ct);
            await row.ReadAsync(ct);
            ctx.Response.Headers["X-Transport-Time"] = row.GetDateTime(3).ToString("O");
            return Results.Ok(new { input.Reference, expiresAt = row.GetDateTime(0),
                xof = row.GetInt32(1), company = row.GetString(2), sandbox });
        }).WithMetadata(Audience.Public).RequireRateLimiting("option-web");
    }

    private static void MapSandbox(WebApplication app, NpgsqlDataSource database)
    {
        app.MapPost("/api/reservations/{reference:guid}/simulation", async (Guid reference, CancellationToken ct) =>
        {
            await using var cmd = database.CreateCommand("SELECT sandbox_charge(@ref)");
            cmd.Parameters.AddWithValue("ref", reference);
            return Results.Ok(new { payment = (Guid)(await cmd.ExecuteScalarAsync(ct))! });
        }).WithMetadata(Audience.Public).RequireRateLimiting("marchand-test");

        app.MapPost("/api/reservations/{reference:guid}/simulation/{payment:guid}", async (
            Guid reference, Guid payment, CancellationToken ct) =>
        {
            // Read the signed ticket after settlement, in a new statement on the same transaction.
            await using var conn = await database.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var settle = new NpgsqlCommand("SELECT outcome FROM reconcile_sandbox(@ref,@payment)", conn, tx);
            settle.Parameters.AddWithValue("ref", reference);
            settle.Parameters.AddWithValue("payment", payment);
            var outcome = (string)(await settle.ExecuteScalarAsync(ct))!;
            await using var proof = new NpgsqlCommand("""
                SELECT d.token,f.id,c.name,b.passenger,b.number,fa.label,tr.departure,e.xof FROM sandbox_receipt r CROSS JOIN company c LEFT JOIN ticket_document d ON d.ticket=r.ticket
                LEFT JOIN sandbox_refund f ON f.payment_id=r.id
                LEFT JOIN cash_entry e ON e.ticket=r.ticket LEFT JOIN booking b ON b.id=e.booking_id
                LEFT JOIN trip tr ON tr.id=b.trip_id LEFT JOIN fare fa ON (fa.trip_id,fa.origin,fa.destination)=(b.trip_id,b.origin,b.destination)
                WHERE r.id=@payment
                """, conn, tx);
            proof.Parameters.AddWithValue("payment", payment);
            string? token;
            Guid? refundId;
            object? journey=null;
            await using (var row = await proof.ExecuteReaderAsync(ct))
            {
                await row.ReadAsync(ct);
                token = row.IsDBNull(0) ? null : row.GetString(0);
                refundId = row.IsDBNull(1) ? null : row.GetGuid(1);
                if (refundId.HasValue) outcome = "refunded";
                if (outcome=="issued") journey=new {company=row.GetString(2),passenger=row.GetString(3),seat=row.GetInt32(4),
                    leg=row.GetString(5),departure=row.GetDateTime(6),xof=row.GetInt32(7)};
            }
            await tx.CommitAsync(ct);
            string? qr = null;
            if (token is not null)
            {
                using var data = QRCodeGenerator.GenerateQrCode(token, QRCodeGenerator.ECCLevel.M);
                using var image = new PngByteQRCode(data);
                qr = Convert.ToBase64String(image.GetGraphic(6));
            }
            return Results.Ok(new { outcome, token, payment, qr, refundId, journey });
        }).WithMetadata(Audience.Public).RequireRateLimiting("marchand-test");
    }

    private sealed record WebRequest(Guid Reference, int TripId, int Number,
        int Origin, int Destination, string? Passenger);
}
