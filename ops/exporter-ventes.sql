\set ON_ERROR_STOP on
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT id AS target FROM company WHERE id=:'company'::uuid AND access_mode='Company' \gset
COPY (
    SELECT :'target'::uuid AS compagnie,e.ticket AS billet,e.booking_id AS reservation,
           to_char(e.paid_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') AS date_utc,
           e.channel AS canal,e.xof AS montant_xof,e.session_id AS caisse,d.agency_id AS agence
    FROM cash_entry e
    LEFT JOIN cash_session s ON s.id=e.session_id
    LEFT JOIN counter_desk d ON d.id=s.desk_id
    WHERE e.paid_at >= (:'jour'::date::timestamp AT TIME ZONE 'UTC')
      AND e.paid_at < ((:'jour'::date+1)::timestamp AT TIME ZONE 'UTC')
    ORDER BY e.paid_at,e.ticket
) TO STDOUT WITH (FORMAT csv, HEADER true);
COMMIT;
