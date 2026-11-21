using Npgsql;

internal static class Schedule
{
    public record Departure(int TemplateId, DateTimeOffset DepartureAt, int CoachNo);

    public static void Map(WebApplication app,NpgsqlDataSource database)
    {
        app.MapGet("/api/programmation/modeles",async (CancellationToken ct) =>
        {
            await using var cmd=database.CreateCommand("""
                SELECT t.id,string_agg(s.name,' → ' ORDER BY s.position)
                FROM trip t JOIN trip_stop s ON s.trip_id=t.id WHERE t.template_id IS NULL
                GROUP BY t.id ORDER BY t.id
                """);
            await using var rows=await cmd.ExecuteReaderAsync(ct);
            var templates=new List<object>();
            while(await rows.ReadAsync(ct))
                templates.Add(new {id=rows.GetInt32(0),label=rows.GetString(1)});
            return Results.Ok(templates);
        });
        app.MapPost("/api/programmation",async (Departure input,CancellationToken ct) =>
        {
            if(input.CoachNo is <1 or >99 || input.DepartureAt==default)
                return Results.BadRequest(new {detail="Choisir une date, une heure et un numéro de car entre 1 et 99."});
            await using var cmd=database.CreateCommand("SELECT schedule_trip(@source,@planned,@coach)");
            cmd.Parameters.AddWithValue("source",input.TemplateId);
            cmd.Parameters.AddWithValue("planned",input.DepartureAt.UtcDateTime);
            cmd.Parameters.AddWithValue("coach",input.CoachNo);
            return Results.Ok(new {id=await cmd.ExecuteScalarAsync(ct)});
        });
    }
}
