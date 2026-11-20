using System.Text.Json;
using Npgsql;

internal static class Departures
{
    public record Dispatch(string Agent);

    public static void Map(WebApplication app, NpgsqlDataSource database)
    {
        app.MapPost("/api/voyages/{trip:int}/depart", async (int trip, Dispatch input, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Agent) || input.Agent.Length>150)
                return Results.BadRequest(new {detail="Agent invalide."});
            await using var cmd=database.CreateCommand("SELECT number,issued_at,agent,passengers::text FROM dispatch_trip(@trip,@agent)");
            cmd.Parameters.AddWithValue("agent",input.Agent.Trim());
            return await Read(cmd,trip,ct);
        });
        app.MapGet("/api/voyages/{trip:int}/manifeste-officiel", async (int trip,CancellationToken ct) =>
        {
            await using var cmd=database.CreateCommand("SELECT number,issued_at,agent,passengers::text FROM departure_manifest WHERE trip_id=@trip");
            return await Read(cmd,trip,ct);
        });
    }

    private static async Task<IResult> Read(NpgsqlCommand cmd,int trip,CancellationToken ct)
    {
        cmd.Parameters.AddWithValue("trip",trip);
        await using var row=await cmd.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return Results.NotFound(new {detail="Aucun manifeste officiel pour ce voyage."});
        return Results.Ok(new {trip,number=row.GetGuid(0),issuedAt=row.GetDateTime(1),agent=row.GetString(2),
            passengers=JsonSerializer.Deserialize<JsonElement>(row.GetString(3))});
    }
}
