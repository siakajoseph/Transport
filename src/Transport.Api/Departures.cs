using System.Text.Json;
using Npgsql;

internal static class Departures
{
    public record Dispatch(string Agent);

    public static void Map(WebApplication app, NpgsqlDataSource database)
    {
        app.MapPost("/api/voyages/{trip:int}/depart", async (int trip, Dispatch input, HttpContext ctx,CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Agent) || input.Agent.Length>150)
                return Results.BadRequest(new {detail="Agent invalide."});
            await using var cmd=database.CreateCommand("SELECT number,issued_at,agent,passengers::text FROM dispatch_trip(checked_departure(@desk,@trip),@agent)");
            cmd.Parameters.AddWithValue("agent",DeskAccess.Current(ctx)?.Signature ?? input.Agent.Trim());
            DeskAccess.Bind(cmd,ctx);
            return await Read(cmd,trip,ct);
        }).WithMetadata(Audience.Dispatch);
        app.MapGet("/api/voyages/{trip:int}/manifeste-officiel", async (int trip,HttpContext ctx,CancellationToken ct) =>
        {
            await using var cmd=database.CreateCommand("SELECT number,issued_at,agent,passengers::text FROM departure_manifest WHERE trip_id=checked_departure(@desk,@trip)");
            DeskAccess.Bind(cmd,ctx);
            return await Read(cmd,trip,ct);
        }).WithMetadata(Audience.Dispatch);
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
