using Npgsql;

internal sealed class Audience
{
    internal static readonly Audience Public = new();
    internal static readonly Audience Profile = new();
    internal static readonly Audience Counter = new("vente");
    internal static readonly Audience Cash = new("vente","fret");
    internal static readonly Audience Freight = new("fret");
    internal static readonly Audience Boarding = new("embarquement");
    internal static readonly Audience PassengerList = new("embarquement","depart");
    internal static readonly Audience Dispatch = new("depart");
    private readonly string[] rights;
    private Audience(params string[] rights) => this.rights=rights;
    internal bool Allows(string[] granted) => rights.Length==0 || rights.Any(granted.Contains);
}

internal sealed record DeskAccess(Guid Id, Guid CompanyId, string Company, string Agency, string Label,
    Guid StaffId,string StaffName,string[] Rights)
{
    internal string CashAgent => "agent/" + StaffId;
    internal string Signature => $"{StaffName} [{StaffId}]";
    internal static void Bind(NpgsqlCommand cmd,HttpContext ctx)
    {
        var desk=Current(ctx);
        cmd.Parameters.AddWithValue("desk",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)desk?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("staff",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)desk?.StaffId ?? DBNull.Value);
    }
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

        var seal=app.Configuration["Transport:AdminSeal"];
        if(mode=="Company" && !string.IsNullOrEmpty(seal))
        {
            if(!AdminAccess.ValidSeal(seal)) throw new InvalidOperationException("Clé serveur administrateur invalide.");
            AdminAccess.Map(app,db,seal);
        }

        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is null) { await next(context); return; }
            if(endpoint.Metadata.GetMetadata<AdminGate>() is { } gate)
            {
                if(await AdminAccess.Authorize(context,db,gate)) await next(context);
                return;
            }
            var audience = endpoint.Metadata.GetMetadata<Audience>();
            var header = context.Request.Headers.Authorization.ToString();
            var personal = context.Request.Headers["X-Transport-Agent"].ToString();
            if (mode == "Demo")
            {
                if (header.Length == 0 && personal.Length == 0) { await next(context); return; }
            }
            else if (header.Length == 0 && personal.Length == 0 && audience == Audience.Public) { await next(context); return; }
            else if (header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase) && header.Length == 71)
            {
                var key = header[7..];
                if (key.All(Uri.IsHexDigit) && personal.Length==64 && personal.All(Uri.IsHexDigit))
                {
                    await using var cmd = db.CreateCommand("""
                        SELECT d.id,c.id,c.name,a.name,d.label,u.id,u.display_name,u.rights FROM counter_desk d JOIN agency a ON a.id=d.agency_id
                        JOIN staff_account u ON u.agency_id=d.agency_id AND u.enabled AND u.credential_hash=digest(@personal,'sha256')
                        CROSS JOIN company c WHERE d.enabled AND d.credential_hash=digest(@key,'sha256')
                        """);
                    cmd.Parameters.AddWithValue("key",key);
                    cmd.Parameters.AddWithValue("personal",personal);
                    DeskAccess? desk;
                    await using (var row = await cmd.ExecuteReaderAsync(context.RequestAborted))
                        desk = await row.ReadAsync(context.RequestAborted) ? new(row.GetGuid(0),row.GetGuid(1),
                            row.GetString(2),row.GetString(3),row.GetString(4),row.GetGuid(5),row.GetString(6),row.GetFieldValue<string[]>(7)) : null;
                    if (desk is not null)
                    {
                        if (audience is null || !audience.Allows(desk.Rights))
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
            await Results.Problem("Identifiants du poste et de l’agent requis.",statusCode:401).ExecuteAsync(context);
        });
        app.MapGet("/api/poste",(HttpContext ctx) =>
        {
            var desk = Current(ctx);
            return Results.Ok(new {mode, companyId=desk?.CompanyId, deskId=desk?.Id,
                company=desk?.Company,agency=desk?.Agency,label=desk?.Label,staffId=desk?.StaffId,staffName=desk?.StaffName,rights=desk?.Rights});
        }).WithMetadata(Audience.Profile);
    }
}
