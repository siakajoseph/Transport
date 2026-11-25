BEGIN;
ALTER TABLE cash_entry ADD COLUMN prior_print_unknown boolean NOT NULL DEFAULT true;
ALTER TABLE cash_entry ALTER COLUMN prior_print_unknown SET DEFAULT false;
CREATE TABLE ticket_print_source (
    ticket uuid PRIMARY KEY REFERENCES cash_entry,
    document jsonb NOT NULL
);
CREATE TABLE ticket_print (
    ticket uuid NOT NULL REFERENCES ticket_print_source,
    sequence integer NOT NULL CHECK(sequence>=0),
    request uuid NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    desk_id uuid REFERENCES counter_desk,
    staff_id uuid REFERENCES staff_account,
    PRIMARY KEY(ticket,sequence), UNIQUE(ticket,request)
);
CREATE FUNCTION prepare_ticket_print(ref uuid, operation uuid, desk uuid, staff uuid)
RETURNS TABLE(copy_number integer,document jsonb) LANGUAGE plpgsql AS $$
DECLARE legacy boolean; n integer;
BEGIN
    SELECT e.prior_print_unknown INTO legacy FROM cash_entry e
    WHERE e.ticket=bag_ticket_for_agent(ref,desk,staff) FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Billet introuvable.'; END IF;
    INSERT INTO ticket_print_source(ticket,document)
    SELECT ref,jsonb_build_object('company',c.name,'passenger',t.passenger,'label',t.label,
        'number',t.number,'xof',t.xof,'paidAt',t.paid_at,'token',t.token,'version',1)
    FROM ticket_document t CROSS JOIN company c WHERE t.ticket=ref ON CONFLICT DO NOTHING;
    SELECT p.sequence INTO n FROM ticket_print p WHERE p.ticket=ref AND p.request=operation;
    IF NOT FOUND THEN
        SELECT coalesce(max(p.sequence)+1,CASE WHEN legacy THEN 1 ELSE 0 END) INTO n
        FROM ticket_print p WHERE p.ticket=ref;
        INSERT INTO ticket_print(ticket,sequence,request,desk_id,staff_id) VALUES(ref,n,operation,desk,staff);
    END IF;
    RETURN QUERY SELECT n,s.document FROM ticket_print_source s WHERE s.ticket=ref;
END $$;
COMMIT;
