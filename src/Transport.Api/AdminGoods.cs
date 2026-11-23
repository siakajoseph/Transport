using Npgsql;

internal static class AdminGoods
{
    internal record Change(string Label,bool Permitted,long Revision);

    internal static void Map(RouteGroupBuilder group,NpgsqlDataSource db)
    {
        group.MapGet("/marchandises",async(CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("SELECT label,permitted,revision FROM freight_goods ORDER BY label");
            await using var rows=await cmd.ExecuteReaderAsync(ct);
            var goods=new List<object>();
            while(await rows.ReadAsync(ct))
                goods.Add(new {label=rows.GetString(0),permitted=rows.GetBoolean(1),revision=rows.GetInt64(2)});
            return Results.Ok(goods);
        });
        group.MapPost("/marchandises",async(Change input,HttpContext ctx,CancellationToken ct)=>{
            var label=input.Label?.Trim();
            if(string.IsNullOrEmpty(label) || label.Length>100 || input.Revision<0)
                return Results.BadRequest(new {detail="Vérifier le libellé de la marchandise."});
            await using var conn=await db.OpenConnectionAsync(ct);
            await using var tx=await conn.BeginTransactionAsync(ct);
            if(!await AdminAccess.SetActor(conn,tx,ctx,ct)) return Results.Unauthorized();
            var change=input.Revision==0
                ? "INSERT INTO freight_goods(label,permitted) VALUES(@label,@allowed) ON CONFLICT DO NOTHING"
                : "UPDATE freight_goods SET permitted=@allowed,revision=revision+1 WHERE label=@label AND revision=@rev AND permitted<>@allowed";
            await using var cmd=new NpgsqlCommand(change,conn,tx);
            cmd.Parameters.AddWithValue("label",label);
            cmd.Parameters.AddWithValue("allowed",input.Permitted);
            cmd.Parameters.AddWithValue("rev",input.Revision);
            if(await cmd.ExecuteNonQueryAsync(ct)!=1)
                return Results.Conflict(new {detail="Catalogue modifié ou règle déjà appliquée. Actualiser le catalogue."});
            cmd.CommandText="""
                INSERT INTO freight_goods_event(label,admin_id,previous,permitted,revision)
                SELECT label,current_setting('transport.admin_id')::uuid,
                    CASE WHEN @rev>0 THEN NOT permitted END,permitted,revision FROM freight_goods WHERE label=@label
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
    }
}
