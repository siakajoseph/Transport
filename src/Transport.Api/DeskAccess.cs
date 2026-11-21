using Npgsql;

internal sealed class Audience
{
    internal static readonly Audience Public = new();
    internal static readonly Audience Counter = new();
    private Audience() {}
}

internal sealed record DeskAccess(Guid Id, Guid CompanyId, string Company, string Agency, string Label)
{
    internal string CashAgent => "poste/" + Id;
    internal static DeskAccess? Current(HttpContext context) => context.Items[typeof(DeskAccess)] as DeskAccess;

    internal static async Task Configure(WebApplication app, NpgsqlDataSource db)
    {
        var mode = app.Configuration["Transport:Access"] ?? "Company";
        if (mode is not ("Demo" or "Company"))
            throw new InvalidOperationException("Profil Transport:Access inconnu.");
        await using var startup = db.CreateCommand("""
            UPDATE company SET access_mode=@mode WHERE access_mode IS NULL OR access_mode=@mode RETURNING id
            """);
        startup.Parameters.AddWithValue("mode", mode);
        if (await startup.ExecuteScalarAsync() is null)
            throw new InvalidOperationException("Le profil configuré diffère du profil de cette base.");

        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is null) { await next(context); return; }
            var audience = endpoint.Metadata.GetMetadata<Audience>();
            var header = context.Request.Headers.Authorization.ToString();
            if (mode == "Demo")
            {
                if (header.Length == 0) { await next(context); return; }
            }
            else if (header.Length == 0 && audience == Audience.Public) { await next(context); return; }
            else if (header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase) && header.Length == 71)
            {
                var key = header[7..];
                if (key.All(Uri.IsHexDigit))
                {
                    await using var cmd = db.CreateCommand("""
                        SELECT d.id,c.id,c.name,a.name,d.label FROM counter_desk d JOIN agency a ON a.id=d.agency_id
                        CROSS JOIN company c WHERE d.enabled AND d.credential_hash=digest(@key,'sha256')
                        """);
                    cmd.Parameters.AddWithValue("key",key);
                    DeskAccess? desk;
                    await using (var row = await cmd.ExecuteReaderAsync(context.RequestAborted))
                        desk = await row.ReadAsync(context.RequestAborted) ? new(row.GetGuid(0),row.GetGuid(1),
                            row.GetString(2),row.GetString(3),row.GetString(4)) : null;
                    if (desk is not null)
                    {
                        if (audience is null)
                        {
                            await Results.Problem("Opération non habilitée pour ce poste.",statusCode:403).ExecuteAsync(context);
                            return;
                        }
                        context.Items[typeof(DeskAccess)] = desk;
                        await next(context);
                        return;
                    }
                }
            }
            await Results.Problem("Authentification du poste requise.",statusCode:401).ExecuteAsync(context);
        });
        app.MapGet("/api/poste",(HttpContext ctx) =>
        {
            var desk = Current(ctx);
            return Results.Ok(new {mode, companyId=desk?.CompanyId, deskId=desk?.Id,
                company=desk?.Company,agency=desk?.Agency,label=desk?.Label});
        }).WithMetadata(Audience.Counter);
    }
}
