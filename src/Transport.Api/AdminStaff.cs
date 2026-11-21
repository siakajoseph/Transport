using System.Text.Json;
using Npgsql;

internal static class AdminStaff
{
    internal sealed record StaffChange(string Name,string[] Rights,bool Enabled,long Revision);
    internal sealed record NewStaff(Guid Id,Guid Agency,string Name,string[] Rights,string KeyHash);
    private static readonly string[] Allowed=["vente","fret","embarquement","depart"];
    private static bool Valid(string? name,string[]? rights) =>
        name?.Trim().Length is >=1 and <=90 && rights is { Length:>=1 and <=4 }
        && rights.All(r=>Allowed.Contains(r)) && rights.Length==rights.Distinct().Count();

    internal static void Map(RouteGroupBuilder group,NpgsqlDataSource db)
    {
        group.MapGet("/annuaire",async(CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("""
                SELECT name FROM company;
                SELECT a.id,a.name,e.name FROM agency a JOIN legal_entity e ON e.id=a.entity_id ORDER BY e.name,a.name,a.id;
                SELECT staff_summary(s)::text FROM staff_account s ORDER BY display_name,id;
                """);
            await using var r=await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            string company=r.GetString(0);
            await r.NextResultAsync(ct);
            var agencies=new List<object>();
            while(await r.ReadAsync(ct)) agencies.Add(new {id=r.GetGuid(0),name=r.GetString(1),entity=r.GetString(2)});
            await r.NextResultAsync(ct);
            var staff=new List<JsonElement>();
            while(await r.ReadAsync(ct)) staff.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));
            return Results.Ok(new {company,agencies,staff});
        });
        group.MapGet("/historique",async(CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("""
                SELECT e.recorded_at,a.login,e.previous::text,e.current_state::text
                FROM staff_admin_event e JOIN company_admin a ON a.id=e.admin_id ORDER BY e.id DESC LIMIT 100
                """);
            await using var r=await cmd.ExecuteReaderAsync(ct);
            var entries=new List<object>();
            while(await r.ReadAsync(ct)) entries.Add(new {at=r.GetDateTime(0),admin=r.GetString(1),
                previous=r.IsDBNull(2)?(JsonElement?)null:JsonSerializer.Deserialize<JsonElement>(r.GetString(2)),
                current=JsonSerializer.Deserialize<JsonElement>(r.GetString(3))});
            return Results.Ok(entries);
        });
        group.MapPost("/agents",async(NewStaff input,HttpContext ctx,CancellationToken ct)=>{
            if(input.Id==Guid.Empty || input.Agency==Guid.Empty || !Valid(input.Name,input.Rights) ||
                input.KeyHash is not {Length:64} || !input.KeyHash.All(Uri.IsHexDigit))
                return Results.BadRequest(new {detail="Vérifier l’agence, le nom, les droits et la clé."});
            await using var conn=await db.OpenConnectionAsync(ct);
            await using var tx=await conn.BeginTransactionAsync(ct);
            if(!await SetActor(conn,tx,ctx,ct)) return Results.Unauthorized();
            await using var cmd=new NpgsqlCommand("""
                INSERT INTO staff_account(id,agency_id,display_name,rights,credential_hash)
                SELECT @id,id,@name,@rights,@hash FROM agency WHERE id=@agency ON CONFLICT DO NOTHING
                """,conn,tx);
            cmd.Parameters.AddWithValue("id",input.Id);
            cmd.Parameters.AddWithValue("agency",input.Agency);
            cmd.Parameters.AddWithValue("name",input.Name.Trim());
            cmd.Parameters.AddWithValue("rights",input.Rights.Order().ToArray());
            cmd.Parameters.AddWithValue("hash",Convert.FromHexString(input.KeyHash));
            await cmd.ExecuteNonQueryAsync(ct);
            cmd.CommandText="""
                SELECT revision FROM staff_account WHERE id=@id AND agency_id=@agency AND display_name=@name
                  AND rights=@rights AND credential_hash=@hash AND revision=1 AND enabled
                """;
            if(await cmd.ExecuteScalarAsync(ct) is null)
                return Results.Conflict(new {detail="Agence ou demande de création incompatible. Actualiser l’annuaire."});
            await tx.CommitAsync(ct);
            return Results.Ok(new {id=input.Id,revision=1});
        });
        group.MapPut("/agents/{id:guid}",async(Guid id,StaffChange input,HttpContext ctx,CancellationToken ct)=>{
            if(input.Revision<=0 || !Valid(input.Name,input.Rights))
                return Results.BadRequest(new {detail="Nom ou droits invalides."});
            await using var conn=await db.OpenConnectionAsync(ct);
            await using var tx=await conn.BeginTransactionAsync(ct);
            if(!await SetActor(conn,tx,ctx,ct)) return Results.Unauthorized();
            await using var cmd=new NpgsqlCommand("""
                UPDATE staff_account SET display_name=@name,rights=@rights,enabled=@enabled
                WHERE id=@id AND revision=@revision RETURNING revision
                """,conn,tx);
            cmd.Parameters.AddWithValue("id",id);
            cmd.Parameters.AddWithValue("revision",input.Revision);
            cmd.Parameters.AddWithValue("name",input.Name.Trim());
            cmd.Parameters.AddWithValue("enabled",input.Enabled);
            cmd.Parameters.AddWithValue("rights",input.Rights.Order().ToArray());
            if(await cmd.ExecuteScalarAsync(ct) is not long revision)
                return Results.Conflict(new {detail="Le compte a changé. Actualiser avant de le modifier."});
            await tx.CommitAsync(ct);
            return Results.Ok(new {id,revision});
        });
    }

    private static async Task<bool> SetActor(NpgsqlConnection conn,NpgsqlTransaction tx,HttpContext ctx,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT set_config('transport.admin_id',a.id::text,true) FROM company_admin a JOIN admin_session s ON s.admin_id=a.id
            WHERE a.id=@id AND a.enabled AND s.token_hash=digest(@token,'sha256') AND s.expires_at>now() FOR SHARE OF a,s
            """,conn,tx);
        cmd.Parameters.AddWithValue("id",(Guid)ctx.Items[typeof(AdminGate)]!);
        cmd.Parameters.AddWithValue("token",ctx.Request.Headers.Authorization.ToString()[7..]);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
