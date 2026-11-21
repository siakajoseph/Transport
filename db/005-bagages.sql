BEGIN;
ALTER TABLE trip ADD COLUMN allowance_g integer NOT NULL DEFAULT 20000 CHECK(allowance_g>=0),
 ADD COLUMN excess_xof_kg integer NOT NULL DEFAULT 500 CHECK(excess_xof_kg BETWEEN 0 AND 100000);
CREATE TABLE bag (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), ticket uuid NOT NULL REFERENCES cash_entry,
 session_id uuid NOT NULL REFERENCES cash_session, grams integer NOT NULL CHECK(grams BETWEEN 1 AND 200000),
 prior_g bigint NOT NULL, fee bigint NOT NULL CHECK(fee>=0), registered_at timestamptz
);
CREATE INDEX bag_ticket ON bag(ticket);
CREATE INDEX bag_shift ON bag(session_id);
CREATE FUNCTION quote_bag(ref uuid, shift uuid, weight_g integer) RETURNS bag LANGUAGE plpgsql AS $$
DECLARE allowance integer; rate integer; prior bigint; result bag;
BEGIN
 PERFORM 1 FROM cash_session WHERE id=shift AND closed_at IS NULL;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Ouvrir la caisse.'; END IF;
 SELECT t.allowance_g,t.excess_xof_kg INTO allowance,rate FROM trip t JOIN booking b ON b.trip_id=t.id
 JOIN cash_entry e ON e.booking_id=b.id WHERE e.ticket=ref AND t.departed_at IS NULL;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Billet inconnu ou voyage parti.'; END IF;
 SELECT coalesce(sum(grams),0) INTO prior FROM bag WHERE ticket=ref AND registered_at IS NOT NULL;
 INSERT INTO bag(ticket,session_id,grams,prior_g,fee) VALUES(ref,shift,weight_g,prior,
 ((greatest(prior+weight_g-allowance,0)+999)/1000-(greatest(prior-allowance,0)+999)/1000)*rate)
 RETURNING * INTO result;
 RETURN result;
END $$;
CREATE FUNCTION register_bag(ref uuid) RETURNS bag LANGUAGE plpgsql AS $$
DECLARE item bag; already bigint;
BEGIN
 SELECT * INTO item FROM bag WHERE id=ref FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Bagage inconnu.'; END IF;
 IF item.registered_at IS NOT NULL THEN RETURN item; END IF;
 PERFORM 1 FROM cash_session WHERE id=item.session_id AND closed_at IS NULL FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Caisse fermée.'; END IF;
 PERFORM 1 FROM cash_entry e JOIN booking b ON b.id=e.booking_id JOIN trip t ON t.id=b.trip_id
 WHERE e.ticket=item.ticket AND t.departed_at IS NULL FOR UPDATE OF e FOR SHARE OF t;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Voyage parti.'; END IF;
 SELECT coalesce(sum(grams),0) INTO already FROM bag WHERE ticket=item.ticket AND registered_at IS NOT NULL;
 IF already<>item.prior_g THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Poids cumulé modifié. Recalculer le supplément.'; END IF;
 UPDATE bag SET registered_at=clock_timestamp() WHERE id=ref RETURNING * INTO item;
 RETURN item;
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
   expected=opening+(SELECT coalesce(sum(fee),0) FROM bag WHERE session_id=shift AND registered_at IS NOT NULL)+(SELECT coalesce(sum(xof),0) FROM cash_entry WHERE session_id=shift)
   WHERE id=shift RETURNING * INTO sess;
 RETURN sess;
END $$;
CREATE OR REPLACE FUNCTION dispatch_trip(t integer, employee text) RETURNS departure_manifest LANGUAGE plpgsql AS $$
DECLARE result departure_manifest; actual timestamptz;
BEGIN
 SELECT departed_at INTO actual FROM trip WHERE id=t FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003', MESSAGE='Voyage inconnu.'; END IF;
 IF actual IS NOT NULL THEN
   SELECT * INTO STRICT result FROM departure_manifest WHERE trip_id=t;
   RETURN result;
 END IF;
 actual := clock_timestamp();
 UPDATE trip SET departed_at=actual WHERE id=t;
 INSERT INTO departure_manifest(trip_id,issued_at,agent,passengers)
 SELECT t,actual,employee,coalesce(jsonb_agg(jsonb_build_object(
   'passenger',b.passenger,'seat',b.number,'leg',f.label,'boardedAt',g.scanned_at,'ticket',e.ticket,'bags',(SELECT coalesce(jsonb_agg(jsonb_build_object('id',z.id,'grams',z.grams) ORDER BY z.registered_at,z.id),'[]'::jsonb)
   FROM bag z WHERE z.ticket=e.ticket AND z.registered_at IS NOT NULL))
   ORDER BY b.origin,b.number,b.id),'[]'::jsonb)
 FROM cash_entry e JOIN booking b ON b.id=e.booking_id JOIN fare f USING(trip_id,origin,destination)
 LEFT JOIN boarding g ON g.ticket=e.ticket WHERE b.trip_id=t RETURNING * INTO result;
 RETURN result;
END $$;
COMMIT;
