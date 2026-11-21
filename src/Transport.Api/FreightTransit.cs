using Npgsql;
using System.Text.RegularExpressions;

internal static class FreightTransit
{
    public record Movement(int TripId,int Stop,string Agent);
    public record Pickup(int TripId,int Stop,Guid Session,string Code,string IdSuffix,long Amount);
    public static void Map(WebApplication app,NpgsqlDataSource db)
    {
        app.MapGet("/api/fret/arrets",async(CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("""
                SELECT s.trip_id,s.position,s.name,t.departure FROM trip_stop s JOIN trip t ON t.id=s.trip_id
                WHERE EXISTS(SELECT FROM freight_prereg p JOIN freight_deposit d USING(reference)
                    WHERE p.trip_id=s.trip_id AND (s.position=p.origin OR s.position=p.destination))
                ORDER BY t.departure DESC,s.trip_id,s.position
                """);
            var stops=new List<object>();
            await using var row=await cmd.ExecuteReaderAsync(ct);
            while(await row.ReadAsync(ct)) stops.Add(new {tripId=row.GetInt32(0),stop=row.GetInt32(1),name=row.GetString(2),departure=row.GetDateTime(3)});
            return Results.Ok(stops);
        });
        app.MapGet("/api/fret/dossiers/{id:guid}/etat",async(Guid id,CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("""
                SELECT CASE WHEN h.reference IS NOT NULL THEN 'Livré' WHEN a.reference IS NOT NULL THEN 'En attente de retrait'
                    WHEN l.reference IS NOT NULL AND t.departed_at IS NOT NULL THEN 'En transit'
                    WHEN l.reference IS NOT NULL THEN 'Chargé' ELSE 'Déposé' END,
                    CASE WHEN d.payment='dû' AND h.reference IS NULL THEN d.xof ELSE 0 END,d.recipient_name
                FROM freight_deposit d JOIN freight_prereg p USING(reference) JOIN trip t ON t.id=p.trip_id
                LEFT JOIN freight_load l USING(reference) LEFT JOIN freight_arrival a USING(reference)
                LEFT JOIN freight_handover h USING(reference) WHERE d.reference=@id
                """);
            cmd.Parameters.AddWithValue("id",id);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)) return Results.NotFound(new {detail="Colis non déposé."});
            return Results.Ok(new {state=row.GetString(0),due=row.GetInt64(1),recipient=row.GetString(2)});
        });
        app.MapPost("/api/fret/dossiers/{id:guid}/mouvements/{action}",async(Guid id,string action,Movement input,CancellationToken ct)=>{
            if(action is not ("charger" or "arriver") || string.IsNullOrWhiteSpace(input.Agent) || input.Agent.Length>150)
                return Results.BadRequest(new {detail="Mouvement ou agent invalide."});
            await using var cmd=db.CreateCommand("SELECT move_freight(@id,@trip,@stop,@action,@agent)");
            cmd.Parameters.AddWithValue("id",id);
            cmd.Parameters.AddWithValue("trip",input.TripId);
            cmd.Parameters.AddWithValue("stop",input.Stop);
            cmd.Parameters.AddWithValue("action",action);
            cmd.Parameters.AddWithValue("agent",input.Agent.Trim());
            return Results.Ok(new {recordedAt=await cmd.ExecuteScalarAsync(ct)});
        });
        app.MapPost("/api/fret/dossiers/{id:guid}/retrait",async(Guid id,Pickup input,CancellationToken ct)=>{
            if(input.Session==Guid.Empty || !Regex.IsMatch(input.Code??"", @"\A[A-Fa-f0-9]{12}\z") ||
               !Regex.IsMatch(input.IdSuffix??"", @"\A[A-Za-z0-9]{4}\z"))
                return Results.BadRequest(new {detail="Vérifier le code de retrait et les quatre derniers caractères de la pièce."});
            await using var cmd=db.CreateCommand("SELECT handed_at,collected FROM hand_over_freight(@id,@trip,@stop,@session,@code,@suffix,@amount)");
            cmd.Parameters.AddWithValue("id",id);
            cmd.Parameters.AddWithValue("trip",input.TripId);
            cmd.Parameters.AddWithValue("stop",input.Stop);
            cmd.Parameters.AddWithValue("session",input.Session);
            cmd.Parameters.AddWithValue("code",input.Code!.ToUpperInvariant());
            cmd.Parameters.AddWithValue("suffix",input.IdSuffix!.ToUpperInvariant());
            cmd.Parameters.AddWithValue("amount",input.Amount);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            await row.ReadAsync(ct);
            return Results.Ok(new {handedAt=row.GetDateTime(0),collected=row.GetInt64(1)});
        });
    }
}
