using Npgsql;

internal static class SandboxRefunds
{
    private sealed record Decision(string? Reason);

    internal static void Map(RouteGroupBuilder admin, NpgsqlDataSource db)
    {
        admin.MapGet("/rapprochement", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await AdminAccess.SetActor(conn, tx, ctx, ct)) return Results.Unauthorized();
            var actor = (Guid)ctx.Items[typeof(AdminGate)]!;
            await using var cmd = new NpgsqlCommand("SELECT sandbox_refunds FROM company_admin WHERE id=@actor", conn, tx);
            cmd.Parameters.AddWithValue("actor", actor);
            if (await cmd.ExecuteScalarAsync(ct) is not true) return Results.StatusCode(403);
            cmd.CommandText = """
                SELECT r.id,r.received_at,r.amount,r.currency,b.xof
                FROM sandbox_receipt r JOIN web_order w USING(reference) JOIN booking b ON b.id=w.booking_id
                WHERE r.outcome='review' AND NOT EXISTS(SELECT FROM sandbox_refund f WHERE f.payment_id=r.id)
                ORDER BY r.received_at,r.id LIMIT 50
                """;
            var pending = new List<object>();
            await using var rows = await cmd.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
                pending.Add(new { payment=rows.GetGuid(0), receivedAt=rows.GetDateTime(1), amount=rows.GetInt32(2),
                    currency=rows.GetString(3), expectedXof=rows.GetInt32(4) });
            return Results.Ok(pending);
        });
        admin.MapPost("/rapprochement/{payment:guid}/remboursement", async (Guid payment, Decision decision, HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Trim().Length > 200)
                return Results.BadRequest(new { detail="Indiquer un motif de 1 à 200 caractères." });
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await AdminAccess.SetActor(conn, tx, ctx, ct)) return Results.Unauthorized();
            await using var cmd = new NpgsqlCommand("SELECT id,amount,currency FROM refund_sandbox(@payment,@actor,@reason)", conn, tx);
            cmd.Parameters.AddWithValue("payment", payment);
            cmd.Parameters.AddWithValue("actor", (Guid)ctx.Items[typeof(AdminGate)]!);
            cmd.Parameters.AddWithValue("reason", decision.Reason.Trim());
            try
            {
                object refund;
                await using (var row = await cmd.ExecuteReaderAsync(ct))
                {
                    await row.ReadAsync(ct);
                    refund = new { id=row.GetGuid(0), amount=row.GetInt32(1), currency=row.GetString(2) };
                }
                await tx.CommitAsync(ct);
                return Results.Ok(refund);
            }
            catch (PostgresException ex) when (ex.SqlState=="P1012") { return Results.StatusCode(403); }
        });
    }
}
