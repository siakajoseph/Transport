using System.Text.Json;
using Npgsql;

internal static class TicketPrinting
{
    internal static void Map(WebApplication app,NpgsqlDataSource db)
    {
        app.MapPost("/api/billets/{ticket:guid}/impressions/{request:guid}",async(Guid ticket,Guid request,HttpContext ctx,CancellationToken ct)=>{
            if (request==Guid.Empty) return Results.BadRequest();
            await using var cmd=db.CreateCommand("SELECT copy_number,document::text FROM prepare_ticket_print(@ticket,@request,@desk,@staff)");
            DeskAccess.Bind(cmd,ctx);
            cmd.Parameters.AddWithValue("ticket",ticket);
            cmd.Parameters.AddWithValue("request",request);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            await row.ReadAsync(ct);
            return Results.Ok(new {sequence=row.GetInt32(0),document=JsonSerializer.Deserialize<JsonElement>(row.GetString(1))});
        }).WithMetadata(Audience.Counter);

        app.MapGet("/api/billets/{ticket:guid}/impressions",async(Guid ticket,HttpContext ctx,CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("""
                SELECT p.sequence,p.recorded_at FROM ticket_print p
                WHERE p.ticket=bag_ticket_for_agent(@ticket,@desk,@staff) ORDER BY p.sequence DESC LIMIT 50
                """);
            DeskAccess.Bind(cmd,ctx);
            cmd.Parameters.AddWithValue("ticket",ticket);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            var entries=new List<object>();
            while(await row.ReadAsync(ct)) entries.Add(new {sequence=row.GetInt32(0),at=row.GetDateTime(1)});
            return Results.Ok(entries);
        }).WithMetadata(Audience.Counter);
    }
}
