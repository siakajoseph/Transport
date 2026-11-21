BEGIN;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE TABLE cash_session (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), agent text NOT NULL,
 opening integer NOT NULL CHECK(opening >= 0), opened_at timestamptz NOT NULL DEFAULT now(),
 closed_at timestamptz, counted integer CHECK(counted >= 0), expected bigint
);
CREATE UNIQUE INDEX cash_session_open ON cash_session(agent) WHERE closed_at IS NULL;
ALTER TABLE cash_entry ADD COLUMN session_id uuid REFERENCES cash_session;
CREATE INDEX cash_entry_session ON cash_entry(session_id);
CREATE TABLE ticket_secret (singleton boolean PRIMARY KEY CHECK(singleton), secret bytea NOT NULL);
INSERT INTO ticket_secret VALUES(true,gen_random_bytes(32));
CREATE FUNCTION collect_cash(option_id uuid, shift uuid) RETURNS cash_entry LANGUAGE plpgsql AS $$
DECLARE previous cash_entry; sess cash_session;
BEGIN
 SELECT * INTO sess FROM cash_session WHERE id=shift FOR UPDATE;
 SELECT * INTO previous FROM cash_entry WHERE booking_id=option_id;
 IF FOUND AND previous.session_id IS NOT DISTINCT FROM shift THEN RETURN previous; END IF;
 IF sess.id IS NULL OR sess.closed_at IS NOT NULL OR previous.ticket IS NOT NULL THEN
   RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Vérifier la session de caisse.';
 END IF;
 SELECT * INTO previous FROM confirm_cash(option_id);
 UPDATE cash_entry SET session_id=shift WHERE ticket=previous.ticket AND session_id IS NULL RETURNING * INTO previous;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Encaissement déjà affecté.'; END IF;
 RETURN previous;
END $$;
CREATE FUNCTION close_shift(shift uuid, total integer) RETURNS cash_session LANGUAGE plpgsql AS $$
DECLARE sess cash_session;
BEGIN
 SELECT * INTO sess FROM cash_session WHERE id=shift FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003', MESSAGE='Caisse inconnue.'; END IF;
 IF sess.closed_at IS NOT NULL THEN
   IF sess.counted <> total THEN RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Comptage déjà validé.'; END IF;
   RETURN sess;
 END IF;
 UPDATE cash_session SET counted=total, closed_at=clock_timestamp(),
   expected=opening+(SELECT coalesce(sum(xof),0) FROM cash_entry WHERE session_id=shift)
   WHERE id=shift RETURNING * INTO sess;
 RETURN sess;
END $$;
CREATE VIEW ticket_document AS
 SELECT e.ticket,e.xof,e.paid_at,b.passenger,b.number,f.label,e.ticket::text || '.' ||
 encode(hmac(convert_to(e.ticket::text,'UTF8'),s.secret,'sha256'),'hex') AS token
 FROM cash_entry e JOIN booking b ON b.id=e.booking_id JOIN fare f USING(trip_id,origin,destination)
 CROSS JOIN ticket_secret s;
COMMIT;
