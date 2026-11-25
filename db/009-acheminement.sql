BEGIN;
CREATE TABLE freight_load (
 reference uuid PRIMARY KEY REFERENCES freight_deposit, loaded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 loader text NOT NULL
);
CREATE TABLE freight_arrival (
 reference uuid PRIMARY KEY REFERENCES freight_load, arrived_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 receiver text NOT NULL, pickup_digest bytea NOT NULL
);
-- Local outbox for the future recipient SMS adapter. No read access via the public API.
CREATE TABLE freight_pickup_message (
 reference uuid PRIMARY KEY REFERENCES freight_arrival, phone text NOT NULL, code text NOT NULL
);
CREATE TABLE freight_handover (
 reference uuid PRIMARY KEY REFERENCES freight_arrival, handed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 session_id uuid NOT NULL REFERENCES cash_session, document_suffix text NOT NULL, collected bigint NOT NULL CHECK(collected>=0)
);
CREATE INDEX freight_handover_session ON freight_handover(session_id,handed_at);
CREATE VIEW freight_cash AS
 SELECT session_id,deposited_at AS booked_at,xof FROM freight_deposit WHERE payment='payé'
 UNION ALL SELECT session_id,handed_at,collected FROM freight_handover WHERE collected>0;

CREATE FUNCTION move_freight(ref uuid, voyage integer, station integer, action text, operator_name text)
RETURNS timestamptz LANGUAGE plpgsql AS $$
DECLARE route freight_prereg; parcel freight_deposit; departed timestamptz; stamp timestamptz; secret text;
BEGIN
 SELECT * INTO parcel FROM freight_deposit WHERE reference=ref FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Colis déposé introuvable.'; END IF;
 SELECT * INTO STRICT route FROM freight_prereg WHERE reference=ref;
 IF route.trip_id<>voyage OR station<>(CASE WHEN action='charger' THEN route.origin ELSE route.destination END) THEN
   RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Voyage ou arrêt incorrect.';
 END IF;
 SELECT departed_at INTO departed FROM trip WHERE id=voyage FOR SHARE;
 IF action='charger' THEN
   SELECT loaded_at INTO stamp FROM freight_load WHERE reference=ref;
   IF FOUND THEN RETURN stamp; END IF;
   IF departed IS NOT NULL THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Voyage déjà parti.'; END IF;
   INSERT INTO freight_load(reference,loader) VALUES(ref,operator_name) RETURNING loaded_at INTO stamp;
 ELSIF action='arriver' THEN
   SELECT arrived_at INTO stamp FROM freight_arrival WHERE reference=ref;
   IF FOUND THEN RETURN stamp; END IF;
   IF departed IS NULL OR NOT EXISTS(SELECT FROM freight_load WHERE reference=ref) THEN
     RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Chargement et départ requis.';
   END IF;
   secret:=upper(encode(gen_random_bytes(6),'hex'));
   INSERT INTO freight_arrival(reference,receiver,pickup_digest) VALUES(ref,operator_name,digest(secret,'sha256')) RETURNING arrived_at INTO stamp;
   INSERT INTO freight_pickup_message VALUES(ref,parcel.recipient_phone,secret);
 ELSE RAISE EXCEPTION USING ERRCODE='P1001',MESSAGE='Action inconnue.';
 END IF;
 RETURN stamp;
END $$;

CREATE FUNCTION hand_over_freight(ref uuid,voyage integer,station integer,shift uuid,secret text,suffix text,agreed bigint)
RETURNS freight_handover LANGUAGE plpgsql AS $$
DECLARE result freight_handover; due bigint; expected_digest bytea;
BEGIN
 SELECT a.pickup_digest,CASE WHEN d.payment='dû' THEN d.xof ELSE 0 END INTO expected_digest,due
 FROM freight_arrival a JOIN freight_deposit d USING(reference) JOIN freight_prereg p USING(reference)
 WHERE a.reference=ref AND p.trip_id=voyage AND p.destination=station FOR UPDATE OF a;
 IF NOT FOUND OR expected_digest IS DISTINCT FROM digest(secret,'sha256') THEN
    RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Vérifier le code, le voyage, la destination et l’arrivée.';
 END IF;
 IF due<>agreed THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Vérifier le montant du retrait.'; END IF;
 SELECT * INTO result FROM freight_handover WHERE reference=ref;
 IF FOUND THEN
    IF result.session_id<>shift OR result.document_suffix<>suffix THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Remise déjà effectuée.'; END IF;
    RETURN result;
 END IF;
 PERFORM 1 FROM cash_session WHERE id=shift AND closed_at IS NULL FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Caisse de remise fermée.'; END IF;
 INSERT INTO freight_handover(reference,session_id,document_suffix,collected) VALUES(ref,shift,suffix,due) RETURNING * INTO result;
 DELETE FROM freight_pickup_message WHERE reference=ref;
 RETURN result;
END $$;
CREATE OR REPLACE FUNCTION close_shift(shift uuid, total integer) RETURNS cash_session LANGUAGE plpgsql AS $$
DECLARE sess cash_session;
BEGIN
 SELECT * INTO sess FROM cash_session WHERE id=shift FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003', MESSAGE='Caisse inconnue.'; END IF;
 IF sess.closed_at IS NOT NULL THEN
   IF sess.counted <> total THEN RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Comptage déjà validé.'; END IF;
   RETURN sess;
 END IF;
 UPDATE cash_session SET counted=total, closed_at=clock_timestamp(),
   expected=opening+(SELECT coalesce(sum(xof),0) FROM freight_cash WHERE session_id=shift)+(SELECT coalesce(sum(fee),0) FROM bag WHERE session_id=shift AND registered_at IS NOT NULL)+(SELECT coalesce(sum(xof),0) FROM cash_entry WHERE session_id=shift)
   WHERE id=shift RETURNING * INTO sess;
 RETURN sess;
END $$;
COMMIT;
