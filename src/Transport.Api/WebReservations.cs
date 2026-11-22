using Npgsql;

internal static class WebReservations
{
    internal static void Map(WebApplication app, NpgsqlDataSource database)
    {
        app.MapPost("/api/reservations", async (WebRequest input, CancellationToken ct) =>
        {
            if (input.Reference == Guid.Empty || string.IsNullOrWhiteSpace(input.Passenger)
                || input.Passenger.Length > 100)
                return Results.BadRequest(new { detail = "Référence ou nom du passager invalide." });

            await using var cmd = database.CreateCommand("""
                SELECT expires_at, xof, c.name FROM hold_web(@ref,@trip,@seat,@origin,@destination,@who)
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
            return Results.Ok(new { input.Reference, expiresAt = row.GetDateTime(0),
                xof = row.GetInt32(1), company = row.GetString(2) });
        }).WithMetadata(Audience.Public).RequireRateLimiting("option-web");
    }

    private sealed record WebRequest(Guid Reference, int TripId, int Number,
        int Origin, int Destination, string? Passenger);
}
