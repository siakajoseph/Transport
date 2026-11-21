using System.Text.Json;
using Npgsql;

internal static class FreightSpace
{
    public record Limits(long Grams,long Cm3);

    public static void Map(WebApplication app,NpgsqlDataSource db)
    {
        app.MapGet("/api/voyages/{id:int}/soute",async(int id,CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("SELECT freight_limit_g,freight_limit_cm3,used_g,used_cm3 FROM freight_space WHERE id=@id");
            cmd.Parameters.AddWithValue("id",id);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)) return Results.NotFound(new {detail="Voyage inconnu."});
            return Results.Ok(new {grams=row.GetInt64(0),cm3=row.GetInt64(1),usedGrams=row.GetInt64(2),usedCm3=row.GetInt64(3)});
        });
        app.MapPut("/api/voyages/{id:int}/soute",async(int id,Limits capacity,CancellationToken ct)=>{
            if(capacity.Grams is <0 or >1000000000 || capacity.Cm3 is <0 or >1000000000)
                return Results.BadRequest(new {detail="Capacité hors limites."});
            await using var conn=await db.OpenConnectionAsync(ct);
            await using var tx=await conn.BeginTransactionAsync(ct);
            await using var guard=new NpgsqlCommand("SELECT id FROM trip WHERE id=@id AND departed_at IS NULL FOR NO KEY UPDATE",conn,tx);
            guard.Parameters.AddWithValue("id",id);
            if(await guard.ExecuteScalarAsync(ct) is null) return Results.Conflict(new {detail="Voyage absent ou parti."});
            await using var cmd=new NpgsqlCommand("""
                UPDATE trip t SET freight_limit_g=@grams,freight_limit_cm3=@volume FROM freight_space s
                WHERE t.id=@id AND s.id=t.id AND s.used_g<=@grams AND s.used_cm3<=@volume
                """,conn,tx);
            cmd.Parameters.AddWithValue("id",id);
            cmd.Parameters.AddWithValue("grams",capacity.Grams);
            cmd.Parameters.AddWithValue("volume",capacity.Cm3);
            if(await cmd.ExecuteNonQueryAsync(ct)!=1) return Results.Conflict(new {detail="Capacité inférieure au chargement déjà enregistré."});
            await tx.CommitAsync(ct);
            return Results.Ok(capacity);
        });
        app.MapGet("/api/voyages/{id:int}/manifeste-fret",async(int id,CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("""
                SELECT number,issued_at,agent,used_g,used_cm3,parcels::text FROM freight_manifest WHERE trip_id=@id
                """);
            cmd.Parameters.AddWithValue("id",id);
            await using var r=await cmd.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return Results.NotFound(new {detail="Aucun manifeste fret émis pour ce voyage."});
            return Results.Ok(new {number=r.GetGuid(0),issuedAt=r.GetDateTime(1),agent=r.GetString(2),grams=r.GetInt64(3),
                cm3=r.GetInt64(4),parcels=JsonSerializer.Deserialize<JsonElement>(r.GetString(5))});
        });
    }
}
