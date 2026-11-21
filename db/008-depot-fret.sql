BEGIN;
CREATE TABLE freight_deposit (
 reference uuid PRIMARY KEY REFERENCES freight_prereg(reference),
 deposited_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 sender_name text NOT NULL, sender_phone text NOT NULL,
 recipient_name text NOT NULL, recipient_phone text NOT NULL,
 grams integer NOT NULL CHECK(grams BETWEEN 1 AND 1000000),
 length_cm integer NOT NULL CHECK(length_cm BETWEEN 1 AND 500),
 width_cm integer NOT NULL CHECK(width_cm BETWEEN 1 AND 500),
 height_cm integer NOT NULL CHECK(height_cm BETWEEN 1 AND 500),
 nature text NOT NULL CHECK(nature IN ('Standard','Fragile','Périssable')),
 payment text NOT NULL CHECK(payment IN ('payé','dû')),
 session_id uuid NOT NULL REFERENCES cash_session,
 kg integer NOT NULL CHECK(kg>0), xof bigint NOT NULL CHECK(xof>0)
);
CREATE INDEX freight_deposit_session ON freight_deposit(session_id,deposited_at) WHERE payment='payé';

CREATE FUNCTION freight_weight(g integer,l integer,w integer,h integer,divisor integer)
RETURNS integer LANGUAGE sql IMMUTABLE STRICT AS $$
 SELECT ceil(greatest(g/1000.0,l::numeric*w*h/divisor))::integer;
$$;

CREATE FUNCTION receive_parcel(ref uuid, shift uuid, g integer,l integer,w integer,h integer,
 sender text, phone_s text, recipient text, phone_r text, category text, method text, agreed bigint)
RETURNS freight_deposit LANGUAGE plpgsql AS $$
DECLARE item freight_prereg; result freight_deposit; billed integer; amount bigint;
BEGIN
 SELECT * INTO item FROM freight_prereg WHERE reference=ref FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Pré-enregistrement introuvable.'; END IF;
 SELECT * INTO result FROM freight_deposit WHERE reference=ref;
 IF FOUND THEN
   IF (result.session_id,result.grams,result.length_cm,result.width_cm,result.height_cm,result.sender_name,
       result.sender_phone,result.recipient_name,result.recipient_phone,result.nature,result.payment,result.xof)
       IS DISTINCT FROM (shift,g,l,w,h,sender,phone_s,recipient,phone_r,category,method,agreed) THEN
     RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Colis déjà déposé avec des informations différentes.';
   END IF;
   RETURN result;
 END IF;
 PERFORM 1 FROM cash_session WHERE id=shift AND closed_at IS NULL FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Ouvrir la caisse avant le dépôt.'; END IF;
 SELECT freight_weight(g,l,w,h,r.divisor),r.xof_per_kg::bigint*freight_weight(g,l,w,h,r.divisor)
 INTO billed,amount FROM trip t JOIN freight_rate r ON r.trip_id=coalesce(t.template_id,t.id)
 WHERE t.id=item.trip_id AND t.departed_at IS NULL AND t.departure>clock_timestamp()
   AND r.origin=item.origin AND r.destination=item.destination FOR SHARE OF t,r;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Départ fermé ou tarif indisponible.'; END IF;
 IF agreed<>amount THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Tarif modifié. Recalculer avant confirmation.'; END IF;
 INSERT INTO freight_deposit(reference,session_id,grams,length_cm,width_cm,height_cm,sender_name,sender_phone,
   recipient_name,recipient_phone,nature,payment,kg,xof)
 VALUES(ref,shift,g,l,w,h,sender,phone_s,recipient,phone_r,category,method,billed,amount) RETURNING * INTO result;
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
   expected=opening+(SELECT coalesce(sum(xof),0) FROM freight_deposit WHERE session_id=shift AND payment='payé')+(SELECT coalesce(sum(fee),0) FROM bag WHERE session_id=shift AND registered_at IS NOT NULL)+(SELECT coalesce(sum(xof),0) FROM cash_entry WHERE session_id=shift)
   WHERE id=shift RETURNING * INTO sess;
 RETURN sess;
END $$;
COMMIT;
