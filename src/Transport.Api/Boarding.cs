using Npgsql;

internal static class Boarding
{
    public record Scan(int TripId, int Stop, string Token, string Agent);

    public static void Map(WebApplication app, NpgsqlDataSource database)
    {
        app.MapGet("/api/embarquement/arrets", async (CancellationToken ct) =>
        {
            await using var cmd = database.CreateCommand("""
                SELECT s.trip_id,s.position,s.name,t.departure,t.departed_at IS NOT NULL FROM trip_stop s JOIN trip t ON t.id=s.trip_id
                WHERE t.departure + interval '1 day' > now()
                AND EXISTS(SELECT FROM fare f WHERE f.trip_id=s.trip_id AND f.origin=s.position)
                ORDER BY t.departure,s.trip_id,s.position
                """);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var stops = new List<object>();
            while (await reader.ReadAsync(ct))
                stops.Add(new {tripId=reader.GetInt32(0),stop=reader.GetInt32(1),name=reader.GetString(2),departure=reader.GetDateTime(3),inTransit=reader.GetBoolean(4)});
            return Results.Ok(stops);
        });

        app.MapPost("/api/embarquement", async (Scan scan, CancellationToken ct) =>
        {
            if (scan.Token?.Length != 101 || !Guid.TryParseExact(scan.Token[..36],"D",out var ticket) ||
                string.IsNullOrWhiteSpace(scan.Agent) || scan.Agent.Length > 150)
                return Results.BadRequest(new {detail="Lecture du QR ou agent invalide."});
            await using var cmd = database.CreateCommand("SELECT scanned_at FROM board_passenger(@trip,@stop,@ticket,@proof,@agent)");
            cmd.Parameters.AddWithValue("trip",scan.TripId);
            cmd.Parameters.AddWithValue("stop",scan.Stop);
            cmd.Parameters.AddWithValue("ticket",ticket);
            cmd.Parameters.AddWithValue("proof",scan.Token);
            cmd.Parameters.AddWithValue("agent",scan.Agent.Trim());
            var scannedAt=await cmd.ExecuteScalarAsync(ct);
            return Results.Ok(new {ticket,scannedAt});
        });

        app.MapGet("/api/voyages/{trip:int}/manifeste", async (int trip, CancellationToken ct) =>
        {
            await using var cmd = database.CreateCommand("""
                SELECT b.passenger,b.number,f.label,g.scanned_at,
                    (SELECT coalesce(jsonb_agg(jsonb_build_object('id',z.id,'grams',z.grams) ORDER BY z.registered_at,z.id),'[]'::jsonb)::text
                     FROM bag z WHERE z.ticket=e.ticket AND z.registered_at IS NOT NULL)
                FROM booking b
                JOIN fare f USING(trip_id,origin,destination)
                JOIN cash_entry e ON e.booking_id=b.id LEFT JOIN boarding g ON g.ticket=e.ticket
                WHERE b.trip_id=@trip ORDER BY b.origin,b.number,b.id
                """);
            cmd.Parameters.AddWithValue("trip",trip);
            await using var reader=await cmd.ExecuteReaderAsync(ct);
            var passengers=new List<object>();
            while(await reader.ReadAsync(ct))
                passengers.Add(new {passenger=reader.GetString(0),seat=reader.GetInt32(1),leg=reader.GetString(2),
                    bags=System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(reader.GetString(4)),boardedAt=reader.IsDBNull(3)?(DateTime?)null:reader.GetDateTime(3)});
            return Results.Ok(passengers);
        });
    }
}
