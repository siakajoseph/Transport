using Npgsql;

internal static class SandboxPhone
{
    private static string? Credential(HttpContext ctx)
    {
        var key=ctx.Request.Headers["X-Transport-Telephone"].ToString();
        return key.Length==64 && key.All(Uri.IsHexDigit) ? key : null;
    }

    internal static void Map(WebApplication app, NpgsqlDataSource db)
    {
        var group=app.MapGroup("/api/sms-test").WithMetadata(Audience.Public).RequireRateLimiting("sms-test");

        group.MapPost("/ligne", async (HttpContext ctx,CancellationToken ct) =>
        {
            var key=Credential(ctx);
            if(key is null) return Results.Unauthorized();
            await using var conn=await db.OpenConnectionAsync(ct);
            await using var tx=await conn.BeginTransactionAsync(ct);
            await using var cmd=new NpgsqlCommand("DELETE FROM sandbox_phone WHERE expires_at<=statement_timestamp()",conn,tx);
            await cmd.ExecuteNonQueryAsync(ct);
            cmd.CommandText="""
                INSERT INTO sandbox_phone(key_hash) VALUES(digest(@key,'sha256'))
                ON CONFLICT(key_hash) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("key",key);
            await cmd.ExecuteNonQueryAsync(ct);
            cmd.CommandText="SELECT phone,expires_at FROM sandbox_phone WHERE key_hash=digest(@key,'sha256') FOR SHARE";
            string phone;
            DateTime expiresAt;
            await using(var row=await cmd.ExecuteReaderAsync(ct))
            {
                if(!await row.ReadAsync(ct)) return Results.Conflict();
                phone=row.GetString(0);
                expiresAt=row.GetDateTime(1);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new {phone,expiresAt});
        });

        group.MapGet("/messages", async (HttpContext ctx,CancellationToken ct) =>
        {
            var key=Credential(ctx);
            if(key is null) return Results.Unauthorized();
            await using var cmd=db.CreateCommand("""
                SELECT s.phone,s.expires_at,m.reference,m.code,m.arrived_at,m.destination,m.due
                FROM sandbox_phone s
                LEFT JOIN LATERAL (
                    SELECT q.reference,q.code,a.arrived_at,t.name AS destination,
                           CASE WHEN d.payment='dû' THEN d.xof ELSE 0 END AS due
                    FROM freight_pickup_message q
                    JOIN freight_deposit d USING(reference) JOIN freight_prereg p USING(reference)
                    JOIN freight_arrival a USING(reference)
                    JOIN trip_stop t ON t.trip_id=p.trip_id AND t.position=p.destination
                    WHERE q.phone=s.phone AND d.deposited_at>=s.created_at
                    ORDER BY a.arrived_at DESC,q.reference LIMIT 50
                ) m ON true
                WHERE s.key_hash=digest(@key,'sha256') AND s.expires_at>statement_timestamp()
                ORDER BY m.arrived_at DESC,m.reference
                """);
            cmd.Parameters.AddWithValue("key",key);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)) return Results.Unauthorized();
            var phone=row.GetString(0);
            var expiresAt=row.GetDateTime(1);
            var messages=new List<object>();
            do
            {
                if(!row.IsDBNull(2)) messages.Add(new {reference=row.GetGuid(2),code=row.GetString(3),
                    arrivedAt=row.GetDateTime(4),destination=row.GetString(5),due=row.GetInt64(6)});
            } while(await row.ReadAsync(ct));
            return Results.Ok(new {phone,expiresAt,messages});
        });

        group.MapDelete("/ligne", async (HttpContext ctx,CancellationToken ct) =>
        {
            var key=Credential(ctx);
            if(key is null) return Results.Unauthorized();
            await using var cmd=db.CreateCommand("DELETE FROM sandbox_phone WHERE key_hash=digest(@key,'sha256')");
            cmd.Parameters.AddWithValue("key",key);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });
    }
}
