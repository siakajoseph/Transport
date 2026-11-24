using Npgsql;

internal static class SupportDiagnostic
{
    internal static void Map(WebApplication app, RouteGroupBuilder admins, NpgsqlDataSource db)
    {
        admins.MapGet("/support", async (CancellationToken ct) =>
        {
            await using var cmd = db.CreateCommand("""
                SELECT d.id,d.technician,d.reason,d.expires_at,a.login,r.login,d.consulted_at,
                    CASE WHEN d.consulted_at IS NOT NULL THEN 'Consulté'
                         WHEN d.revoked_at IS NOT NULL THEN 'Révoqué'
                         WHEN d.expires_at<=statement_timestamp() THEN 'Expiré'
                         WHEN d.approved_at IS NOT NULL THEN 'Autorisé' ELSE 'À examiner' END AS state,
                    d.approved_at,d.revoked_at
                FROM support_diagnostic d
                LEFT JOIN company_admin a ON a.id=d.approved_by
                LEFT JOIN company_admin r ON r.id=d.revoked_by
                ORDER BY d.requested_at DESC,d.id DESC LIMIT 100
                """);
            await using var rows = await cmd.ExecuteReaderAsync(ct);
            var requests = new List<object>();
            while (await rows.ReadAsync(ct))
                requests.Add(new { id=rows.GetGuid(0), technician=rows.GetString(1), reason=rows.GetString(2),
                    expiresAt=rows.GetDateTime(3), approvedBy=rows.IsDBNull(4)?null:rows.GetString(4),
                    revokedBy=rows.IsDBNull(5)?null:rows.GetString(5),
                    consultedAt=rows.IsDBNull(6)?(DateTime?)null:rows.GetDateTime(6), state=rows.GetString(7),
                    approvedAt=rows.IsDBNull(8)?(DateTime?)null:rows.GetDateTime(8),
                    revokedAt=rows.IsDBNull(9)?(DateTime?)null:rows.GetDateTime(9) });
            return Results.Ok(requests);
        });

        admins.MapPost("/support/{id:guid}/{decision}", async (Guid id, string decision, HttpContext ctx, CancellationToken ct) =>
        {
            if (decision is not ("autoriser" or "revoquer")) return Results.BadRequest();
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await AdminAccess.SetActor(conn,tx,ctx,ct)) return Results.Unauthorized();
            var update = decision=="autoriser"
                ? " SET approved_by=@actor,approved_at=clock_timestamp() WHERE approved_at IS NULL AND "
                : " SET revoked_by=@actor,revoked_at=clock_timestamp() WHERE ";
            await using var cmd = new NpgsqlCommand("UPDATE support_diagnostic" + update +
                "id=@id AND revoked_at IS NULL AND consulted_at IS NULL AND expires_at>clock_timestamp()",conn,tx);
            cmd.Parameters.AddWithValue("id",id);
            cmd.Parameters.AddWithValue("actor",(Guid)ctx.Items[typeof(AdminGate)]!);
            if (await cmd.ExecuteNonQueryAsync(ct)!=1)
                return Results.Conflict(new { detail="Actualisez les demandes pour vérifier leur état." });
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        app.MapPost("/api/editeur/support/diagnostic", async (HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl="no-store";
            var key = ctx.Request.Headers["X-Transport-Support"].ToString();
            if (key.Length!=64 || !key.All(Uri.IsHexDigit)) return Results.Unauthorized();
            await using var cmd = db.CreateCommand("""
                WITH used AS (
                    UPDATE support_diagnostic SET consulted_at=clock_timestamp()
                    WHERE key_hash=digest(@key,'sha256') AND approved_at IS NOT NULL
                      AND revoked_at IS NULL AND consulted_at IS NULL AND expires_at>clock_timestamp()
                    RETURNING id,consulted_at
                )
                SELECT used.id,used.consulted_at,c.name,c.access_mode,
                    (SELECT count(*) FROM agency),(SELECT count(*) FROM counter_desk WHERE enabled)
                FROM used CROSS JOIN company c
                """);
            cmd.Parameters.AddWithValue("key",key);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return Results.Unauthorized();
            return Results.Ok(new { request=row.GetGuid(0), consultedAt=row.GetDateTime(1), company=row.GetString(2),
                mode=row.GetString(3), agencies=row.GetInt64(4), enabledDesks=row.GetInt64(5) });
        }).WithMetadata(Audience.Public).RequireRateLimiting("editeur");
    }
}
