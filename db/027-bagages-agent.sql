BEGIN;
CREATE FUNCTION bag_ticket_for_agent(ref uuid, desk uuid, staff uuid) RETURNS uuid
LANGUAGE plpgsql STABLE AS $$
BEGIN
    IF desk IS NOT NULL AND NOT EXISTS (
        SELECT FROM cash_entry e JOIN cash_session s ON s.id=e.session_id
        WHERE e.ticket=ref AND s.desk_id=desk AND s.staff_id=staff
    ) THEN
        RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Billet introuvable pour cet agent.';
    END IF;
    RETURN ref;
END $$;
COMMIT;
