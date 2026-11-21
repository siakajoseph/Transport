using Npgsql;

internal static class FreightDepot
{
    public record Receipt(Guid Session,int Grams,int LengthCm,int WidthCm,int HeightCm,
        string Sender,string SenderPhone,string Recipient,string RecipientPhone,string Nature,string Payment,long AgreedXof);

    public static void Map(WebApplication app,NpgsqlDataSource db)
    {
        app.MapGet("/api/fret/dossiers/{id:guid}",async (Guid id,HttpContext ctx,CancellationToken ct)=>
        {
            await using var cmd=db.CreateCommand("""
                SELECT p.trip_id,p.origin,p.destination,f.label,coalesce(d.grams,p.grams),coalesce(d.length_cm,p.length_cm),
                  coalesce(d.width_cm,p.width_cm),coalesce(d.height_cm,p.height_cm),coalesce(d.xof,p.price_xof),d.deposited_at
                FROM freight_prereg p JOIN fare f USING(trip_id,origin,destination)
                LEFT JOIN freight_deposit d USING(reference) WHERE p.reference=@id AND serves_stop(@desk,p.trip_id,p.origin)
                """);
            DeskAccess.Bind(cmd,ctx);
            cmd.Parameters.AddWithValue("id",id);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)) return Results.NotFound(new {detail="Référence inconnue."});
            return Results.Ok(new {tripId=row.GetInt32(0),origin=row.GetInt32(1),destination=row.GetInt32(2),
                leg=row.GetString(3),grams=row.GetInt32(4),lengthCm=row.GetInt32(5),widthCm=row.GetInt32(6),
                heightCm=row.GetInt32(7),xof=row.GetInt64(8),depositedAt=row.IsDBNull(9)?(DateTime?)null:row.GetDateTime(9)});
        }).WithMetadata(Audience.Freight);

        app.MapPost("/api/fret/dossiers/{id:guid}/depot",async (Guid id,Receipt input,HttpContext ctx,CancellationToken ct)=>
        {
            if (input.Session==Guid.Empty || input.Grams is <1 or >1000000 ||
                new[]{input.LengthCm,input.WidthCm,input.HeightCm}.Any(n=>n is <1 or >500) ||
                new[]{input.Sender,input.Recipient}.Any(n=>string.IsNullOrWhiteSpace(n)||n.Length>100) ||
                new[]{input.SenderPhone,input.RecipientPhone}.Any(n=>n is null || !System.Text.RegularExpressions.Regex.IsMatch(n,@"\A\+?[0-9]{7,15}\z")) ||
                input.Payment is not ("payé" or "dû") || input.Nature is not ("Standard" or "Périssable" or "Fragile"))
                return Results.BadRequest(new {detail="Vérifier les mesures, les contacts, la nature et le mode de paiement."});
            await using var cmd=db.CreateCommand("""
                SELECT kg,xof,deposited_at FROM receive_parcel(checked_parcel(@desk,@id,true),own_shift(@session,@desk,@staff),@g,@l,@w,@h,@s,@sp,@r,@rp,@nature,@payment,@agreed)
                """);
            DeskAccess.Bind(cmd,ctx);
            cmd.Parameters.AddWithValue("id",id);
            cmd.Parameters.AddWithValue("session",input.Session);
            cmd.Parameters.AddWithValue("g",input.Grams);
            cmd.Parameters.AddWithValue("l",input.LengthCm);
            cmd.Parameters.AddWithValue("w",input.WidthCm);
            cmd.Parameters.AddWithValue("h",input.HeightCm);
            cmd.Parameters.AddWithValue("s",input.Sender.Trim());
            cmd.Parameters.AddWithValue("sp",input.SenderPhone);
            cmd.Parameters.AddWithValue("r",input.Recipient.Trim());
            cmd.Parameters.AddWithValue("rp",input.RecipientPhone);
            cmd.Parameters.AddWithValue("nature",input.Nature);
            cmd.Parameters.AddWithValue("payment",input.Payment);
            cmd.Parameters.AddWithValue("agreed",input.AgreedXof);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            await row.ReadAsync(ct);
            return Results.Ok(new {reference=id,kg=row.GetInt32(0),xof=row.GetInt64(1),depositedAt=row.GetDateTime(2)});
        }).WithMetadata(Audience.Freight);
    }
}
