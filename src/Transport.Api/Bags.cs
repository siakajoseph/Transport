using Npgsql;

internal static class Bags
{
    public record Weigh(Guid Session,int Grams);

    public static void Map(WebApplication app,NpgsqlDataSource database)
    {
        app.MapPost("/api/billets/{ticket:guid}/bagages",async (Guid ticket,Weigh input,HttpContext ctx,CancellationToken ct) =>
        {
            if (input.Grams is <1 or >200000) return Results.BadRequest(new {detail="Saisir un poids entre 1 g et 200 kg."});
            await using var cmd=database.CreateCommand("SELECT id,grams,prior_g,fee,registered_at FROM quote_bag(bag_ticket_for_agent(@ticket,@desk,@staff),own_shift(@session,@desk,@staff),@grams)");
            DeskAccess.Bind(cmd,ctx);
            cmd.Parameters.AddWithValue("ticket",ticket);
            cmd.Parameters.AddWithValue("session",input.Session);
            cmd.Parameters.AddWithValue("grams",input.Grams);
            return await Read(cmd,ct);
        }).WithMetadata(Audience.Counter);
        app.MapPost("/api/bagages/{id:guid}/confirmer",async (Guid id,HttpContext ctx,CancellationToken ct) =>
        {
            await using var cmd=database.CreateCommand("SELECT id,grams,prior_g,fee,registered_at FROM register_bag((SELECT b.id FROM bag b WHERE b.id=@id AND own_shift(b.session_id,@desk,@staff)=b.session_id))");
            DeskAccess.Bind(cmd,ctx);
            cmd.Parameters.AddWithValue("id",id);
            return await Read(cmd,ct);
        }).WithMetadata(Audience.Counter);
        app.MapGet("/api/billets/{ticket:guid}/etiquettes",async (Guid ticket,HttpContext ctx,CancellationToken ct) =>
        {
            await using var cmd=database.CreateCommand("""
                SELECT z.id,z.grams,d.passenger,d.label FROM bag z JOIN ticket_document d USING(ticket)
                WHERE z.ticket=bag_ticket_for_agent(@ticket,@desk,@staff) AND registered_at IS NOT NULL ORDER BY registered_at,z.id
                """);
            DeskAccess.Bind(cmd,ctx);
            cmd.Parameters.AddWithValue("ticket",ticket);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            var labels=new List<object>();
            while(await row.ReadAsync(ct)) labels.Add(new {id=row.GetGuid(0),grams=row.GetInt32(1),passenger=row.GetString(2),leg=row.GetString(3)});
            return Results.Ok(labels);
        }).WithMetadata(Audience.Counter);
    }

    private static async Task<IResult> Read(NpgsqlCommand cmd,CancellationToken ct)
    {
        await using var row=await cmd.ExecuteReaderAsync(ct);
        await row.ReadAsync(ct);
        return Results.Ok(new {id=row.GetGuid(0),grams=row.GetInt32(1),priorGrams=row.GetInt64(2),fee=row.GetInt64(3),registered=!row.IsDBNull(4)});
    }
}
