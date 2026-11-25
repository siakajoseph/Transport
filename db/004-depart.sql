BEGIN;
ALTER TABLE trip ADD COLUMN departed_at timestamptz;
CREATE TABLE departure_manifest (
 trip_id integer PRIMARY KEY REFERENCES trip, number uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
 issued_at timestamptz NOT NULL, agent text NOT NULL, passengers jsonb NOT NULL
);

-- Sales and scans can proceed concurrently until dispatch takes the exclusive trip lock.
CREATE FUNCTION guard_trip_sale() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 PERFORM 1 FROM trip WHERE id=NEW.trip_id AND departed_at IS NULL FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Ventes closes. Voyage en transit.'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER sale_before_departure BEFORE INSERT OR UPDATE OF state ON booking
 FOR EACH ROW WHEN (NEW.state IN ('sold','hold')) EXECUTE FUNCTION guard_trip_sale();

CREATE FUNCTION guard_boarding_stop() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE closed timestamptz; origin_stop integer; first_stop integer;
BEGIN
 SELECT t.departed_at,b.origin,(SELECT min(position) FROM trip_stop WHERE trip_id=t.id)
 INTO STRICT closed,origin_stop,first_stop FROM cash_entry e JOIN booking b ON b.id=e.booking_id
 JOIN trip t ON t.id=b.trip_id WHERE e.ticket=NEW.ticket FOR SHARE OF t;
 IF closed IS NOT NULL AND origin_stop=first_stop THEN
   RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Le véhicule a quitté la gare de départ.';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER boarding_before_departure BEFORE INSERT ON boarding FOR EACH ROW EXECUTE FUNCTION guard_boarding_stop();

CREATE FUNCTION dispatch_trip(t integer, employee text) RETURNS departure_manifest LANGUAGE plpgsql AS $$
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
   'passenger',b.passenger,'seat',b.number,'leg',f.label,'boardedAt',g.scanned_at,'ticket',e.ticket)
   ORDER BY b.origin,b.number,b.id),'[]'::jsonb)
 FROM cash_entry e JOIN booking b ON b.id=e.booking_id JOIN fare f USING(trip_id,origin,destination)
 LEFT JOIN boarding g ON g.ticket=e.ticket WHERE b.trip_id=t RETURNING * INTO result;
 RETURN result;
END $$;
COMMIT;
