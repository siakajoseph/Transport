\set ON_ERROR_STOP on
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT id AS target FROM company WHERE id=:'company'::uuid AND access_mode='Company' \gset
COPY (
    WITH mouvements(nature,reference,piece,instant,canal,montant,devise,caisse) AS (
        SELECT 'billet',ticket,booking_id,paid_at,channel,xof,'XOF',session_id FROM cash_entry
        UNION ALL
        SELECT 'fret_depot',reference,reference,deposited_at,'especes',xof,'XOF',session_id
        FROM freight_deposit WHERE payment='payé' AND xof>0
        UNION ALL
        SELECT 'fret_retrait',reference,reference,handed_at,'especes',collected,'XOF',session_id
        FROM freight_handover WHERE collected>0
        UNION ALL
        SELECT 'bagage',id,ticket,registered_at,'especes',fee,'XOF',session_id
        FROM bag WHERE registered_at IS NOT NULL AND fee>0
        UNION ALL
        SELECT 'remboursement_test',id,payment_id,recorded_at,'sandbox',-amount,currency,NULL::uuid
        FROM sandbox_refund
    )
    SELECT :'target'::uuid AS compagnie,nature,reference,piece,
           to_char(instant AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') AS date_utc,
           canal,montant,devise,caisse,d.agency_id AS agence
    FROM mouvements m LEFT JOIN cash_session s ON s.id=m.caisse
    LEFT JOIN counter_desk d ON d.id=s.desk_id
    WHERE instant >= (:'jour'::date::timestamp AT TIME ZONE 'UTC')
      AND instant < ((:'jour'::date+1)::timestamp AT TIME ZONE 'UTC')
    ORDER BY instant,nature,reference
) TO STDOUT WITH (FORMAT csv, HEADER true);
COMMIT;
