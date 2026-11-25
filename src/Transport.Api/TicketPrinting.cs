using System.Text.Json;
using Npgsql;

internal static class TicketPrinting
{
    internal sealed record Contact(string? Phone);
    internal static string NormalizePhone(string? input)=>(input??"").Trim().Replace(" ","").Replace("-","");
    internal static bool ValidPhone(string phone)=>System.Text.RegularExpressions.Regex.IsMatch(phone,@"\A[+]?[0-9]{7,15}\z");
    internal static void Map(WebApplication app,NpgsqlDataSource db)
    {
        app.MapPost("/api/billets/recherche-telephone",async(Contact contact,HttpContext ctx,CancellationToken ct)=>{
            if(DeskAccess.Current(ctx) is null) return Results.Unauthorized();
            var phone=NormalizePhone(contact.Phone);
            if(!ValidPhone(phone)) return Results.BadRequest(new {detail="Saisir le numéro complet."});
            await using var cmd=db.CreateCommand("""
                SELECT e.ticket,b.passenger,f.label,b.number,t.departure FROM counter_booking_phone p
                JOIN booking b ON b.id=p.booking_id JOIN cash_entry e ON e.booking_id=b.id
                JOIN cash_session s ON s.id=e.session_id JOIN trip t ON t.id=b.trip_id
                JOIN fare f USING(trip_id,origin,destination)
                WHERE p.phone=@phone AND s.desk_id=@desk AND s.staff_id=@staff
                ORDER BY e.paid_at DESC,e.ticket LIMIT 50
                """);
            DeskAccess.Bind(cmd,ctx);
            cmd.Parameters.AddWithValue("phone",phone);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            var matches=new List<object>();
            while(await row.ReadAsync(ct)) matches.Add(new {ticket=row.GetGuid(0),passenger=row.GetString(1),leg=row.GetString(2),
                number=row.GetInt32(3),departure=row.GetDateTime(4)});
            return Results.Ok(matches);
        }).WithMetadata(Audience.Counter);

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
