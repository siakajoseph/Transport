using Npgsql;

internal static class CashReports
{
    public static void Map(WebApplication app, NpgsqlDataSource database)
    {
        app.MapGet("/api/caisse/rapport", async (string agent, DateOnly date, HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(agent) || agent.Length > 150 || date == DateOnly.MaxValue)
                return Results.BadRequest(new {detail="Agent ou date invalide."});
            var start=date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            await using var cmd=database.CreateCommand("""
                SELECT s.id,s.opened_at,s.closed_at,s.opening,
                       t.daily_count,t.daily_amount,b.daily_amount,
                       coalesce(s.expected,s.opening+t.total+b.total+f.total),s.counted,f.daily_amount
                FROM cash_session s
                CROSS JOIN LATERAL (
                    SELECT count(*) FILTER (WHERE paid_at>=@start AND paid_at<@end) daily_count,
                      coalesce(sum(xof) FILTER (WHERE paid_at>=@start AND paid_at<@end),0) daily_amount,
                      coalesce(sum(xof),0) total FROM cash_entry WHERE session_id=s.id
                ) t
                CROSS JOIN LATERAL (
                    SELECT coalesce(sum(fee) FILTER (WHERE registered_at>=@start AND registered_at<@end),0)::bigint daily_amount,
                      coalesce(sum(fee),0)::bigint total FROM bag WHERE session_id=s.id AND registered_at IS NOT NULL
                ) b
                CROSS JOIN LATERAL (
                    SELECT coalesce(sum(xof) FILTER (WHERE booked_at>=@start AND booked_at<@end),0)::bigint daily_amount,
                      coalesce(sum(xof),0)::bigint total FROM freight_cash WHERE session_id=s.id
                ) f
                WHERE s.agent=@agent AND s.desk_id IS NOT DISTINCT FROM @desk AND s.opened_at<@end AND coalesce(s.closed_at,statement_timestamp())>=@start
                ORDER BY s.opened_at,s.id
                """);
            var desk=DeskAccess.Current(ctx);
            cmd.Parameters.AddWithValue("desk",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)desk?.Id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("agent",desk?.CashAgent ?? agent);
            cmd.Parameters.AddWithValue("start",start);
            cmd.Parameters.AddWithValue("end",start.AddDays(1));
            await using var rows=await cmd.ExecuteReaderAsync(ct);
            var sessions=new List<object>();
            while(await rows.ReadAsync(ct))
            {
                long expected=rows.GetInt64(7);
                int? counted=rows.IsDBNull(8)?null:rows.GetInt32(8);
                sessions.Add(new {id=rows.GetGuid(0),openedAt=rows.GetDateTime(1),
                    closedAt=rows.IsDBNull(2)?(DateTime?)null:rows.GetDateTime(2),opening=rows.GetInt32(3),
                    tickets=rows.GetInt64(4),ticketXof=rows.GetInt64(5),bagXof=rows.GetInt64(6),
                    freightXof=rows.GetInt64(9),expected,counted,difference=counted-expected});
            }
            return Results.Ok(sessions);
        }).WithMetadata(Audience.Counter);
    }
}
