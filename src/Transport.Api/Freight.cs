using Npgsql;

internal static class Freight
{
    public record Parcel(int TripId,int Origin,int Destination,int Grams,int LengthCm,int WidthCm,int HeightCm,Guid? RequestId);

    public static void Map(WebApplication app,NpgsqlDataSource db)
    {
        app.MapPost("/api/fret/devis",(Parcel input,CancellationToken ct)=>Calculate(db,input,false,ct));
        app.MapPost("/api/fret/preinscriptions",(Parcel input,CancellationToken ct)=>Calculate(db,input,true,ct));
    }

    private static async Task<IResult> Calculate(NpgsqlDataSource db,Parcel p,bool reserve,CancellationToken ct)
    {
        if(p.Grams is <1 or >1000000 || new[]{p.LengthCm,p.WidthCm,p.HeightCm}.Any(x=>x is <1 or >500)
            || (reserve && (p.RequestId is null || p.RequestId==Guid.Empty)))
            return Results.BadRequest(new {detail="Poids, dimensions ou référence de demande invalides."});

        await using var conn=await db.OpenConnectionAsync(ct);
        await using var tx=await conn.BeginTransactionAsync(ct);
        if(reserve)
        {
            await using var guard=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",conn,tx);
            guard.Parameters.AddWithValue("key",p.RequestId.ToString()!);
            await guard.ExecuteNonQueryAsync(ct);
            await using var previous=new NpgsqlCommand("""
                SELECT reference,chargeable_kg,price_xof,(trip_id,origin,destination,grams,length_cm,width_cm,height_cm)
                    =(@trip,@a,@b,@g,@l,@w,@h) FROM freight_prereg WHERE request_id=@key
                """,conn,tx);
            AddParcel(previous,p);
            await using var rows=await previous.ExecuteReaderAsync(ct);
            if(await rows.ReadAsync(ct))
                return rows.GetBoolean(3)? Results.Ok(new {reference=rows.GetGuid(0),kg=rows.GetInt32(1),xof=rows.GetInt64(2)})
                    :Results.Conflict(new {detail="Cette demande correspond à un autre colis."});
        }
        await using var rate=new NpgsqlCommand("""
            SELECT r.xof_per_kg,r.divisor FROM trip t JOIN freight_rate r ON r.trip_id=coalesce(t.template_id,t.id)
            WHERE t.id=@trip AND r.origin=@a AND r.destination=@b AND t.departed_at IS NULL
            AND t.departure>clock_timestamp() FOR SHARE OF t,r
            """,conn,tx);
        rate.Parameters.AddWithValue("trip",p.TripId);
        rate.Parameters.AddWithValue("a",p.Origin);
        rate.Parameters.AddWithValue("b",p.Destination);
        int kg;
        long xof;
        await using(var rows=await rate.ExecuteReaderAsync(ct))
        {
            if(!await rows.ReadAsync(ct)) return Results.NotFound(new {detail="Trajet fret indisponible."});
            var volumetric=(decimal)p.LengthCm*p.WidthCm*p.HeightCm/rows.GetInt32(1);
            kg=(int)Math.Ceiling(Math.Max(p.Grams/1000m,volumetric));
            xof=(long)kg*rows.GetInt32(0);
        }
        if(!reserve) return Results.Ok(new {kg,xof});
        await using var add=new NpgsqlCommand("""
            INSERT INTO freight_prereg(request_id,trip_id,origin,destination,grams,length_cm,width_cm,height_cm,chargeable_kg,price_xof)
            VALUES(@key,@trip,@a,@b,@g,@l,@w,@h,@kg,@xof) RETURNING reference
            """,conn,tx);
        AddParcel(add,p);
        add.Parameters.AddWithValue("kg",kg);
        add.Parameters.AddWithValue("xof",xof);
        var reference=(Guid)(await add.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return Results.Ok(new {reference,kg,xof});
    }

    private static void AddParcel(NpgsqlCommand cmd,Parcel p)
    {
        cmd.Parameters.AddWithValue("key",p.RequestId!.Value);
        cmd.Parameters.AddWithValue("trip",p.TripId);
        cmd.Parameters.AddWithValue("a",p.Origin);
        cmd.Parameters.AddWithValue("b",p.Destination);
        cmd.Parameters.AddWithValue("g",p.Grams);
        cmd.Parameters.AddWithValue("l",p.LengthCm);
        cmd.Parameters.AddWithValue("w",p.WidthCm);
        cmd.Parameters.AddWithValue("h",p.HeightCm);
    }
}
