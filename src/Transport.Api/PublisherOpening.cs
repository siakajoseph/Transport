using Npgsql;

internal sealed class PublisherOpening
{
    private sealed record Opening(string? Company, string? Entity, string? Country, string? Agency, int Template, int Stop);

    internal static async Task<bool> Authorize(HttpContext ctx, NpgsqlDataSource db)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!ctx.Request.Headers.ContainsKey("X-Transport-Agent") && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && header.Length == 71 && header[7..].All(Uri.IsHexDigit))
        {
            await using var cmd = db.CreateCommand("""
                SELECT id FROM publisher_mandate WHERE key_hash=digest(@key,'sha256')
                AND expires_at>clock_timestamp() AND revoked_at IS NULL
                """);
            cmd.Parameters.AddWithValue("key", header[7..]);
            if (await cmd.ExecuteScalarAsync(ctx.RequestAborted) is Guid id)
            {
                ctx.Items[typeof(PublisherOpening)] = id;
                return true;
            }
        }
        await Results.Unauthorized().ExecuteAsync(ctx);
        return false;
    }

    internal static void Map(WebApplication app, NpgsqlDataSource db)
    {
        var group = app.MapGroup("/api/editeur").WithMetadata(new PublisherOpening()).RequireRateLimiting("editeur");
        group.MapGet("/mandat", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var cmd = db.CreateCommand("""
                SELECT m.company_id,m.operator_name,m.expires_at,o.agency_id FROM publisher_mandate m
                LEFT JOIN company_opening o ON o.mandate_id=m.id WHERE m.id=@id
                """);
            cmd.Parameters.AddWithValue("id", (Guid)ctx.Items[typeof(PublisherOpening)]!);
            Guid companyId;
            string operatorName;
            DateTime expiresAt;
            Guid? agencyId;
            await using (var row = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await row.ReadAsync(ct)) return Results.Unauthorized();
                companyId = row.GetGuid(0);
                operatorName = row.GetString(1);
                expiresAt = row.GetDateTime(2);
                agencyId = row.IsDBNull(3) ? null : row.GetGuid(3);
            }
            await using var stopsCmd = db.CreateCommand("""
                SELECT s.trip_id,s.position,s.name FROM trip_stop s JOIN trip t ON t.id=s.trip_id
                WHERE t.template_id IS NULL ORDER BY s.trip_id,s.position
                """);
            await using var rows = await stopsCmd.ExecuteReaderAsync(ct);
            var stops = new List<object>();
            while (await rows.ReadAsync(ct))
                stops.Add(new { template = rows.GetInt32(0), stop = rows.GetInt32(1), name = rows.GetString(2) });
            return Results.Ok(new { companyId, operatorName, expiresAt, agencyId, stops });
        });

        group.MapPost("/ouverture", async (Opening input, HttpContext ctx, CancellationToken ct) =>
        {
            if (new[] { input.Company, input.Entity, input.Agency }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 120)
                || input.Country?.Length != 2 || !input.Country.All(char.IsAsciiLetter))
                return Results.BadRequest(new { detail = "Vérifiez les noms et le code pays à deux lettres." });

            await using var cmd = db.CreateCommand("SELECT publisher_open(@id,@company,@entity,@country,@agency,@template,@stop)");
            cmd.Parameters.AddWithValue("id", (Guid)ctx.Items[typeof(PublisherOpening)]!);
            cmd.Parameters.AddWithValue("company", input.Company!);
            cmd.Parameters.AddWithValue("entity", input.Entity!);
            cmd.Parameters.AddWithValue("country", input.Country);
            cmd.Parameters.AddWithValue("agency", input.Agency!);
            cmd.Parameters.AddWithValue("template", input.Template);
            cmd.Parameters.AddWithValue("stop", input.Stop);
            try { return Results.Ok(new { agencyId = (Guid)(await cmd.ExecuteScalarAsync(ct))! }); }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            { return Results.Conflict(new { detail = ex.MessageText }); }
            catch (PostgresException ex) when (ex.SqlState == "P1010")
            { return Results.Unauthorized(); }
        });
    }
}
