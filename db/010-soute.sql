BEGIN;
-- Space specifically allocated to freight. Passenger luggage is handled separately.
ALTER TABLE trip ADD COLUMN freight_limit_g bigint NOT NULL DEFAULT 1000000 CHECK(freight_limit_g>=0),
 ADD COLUMN freight_limit_cm3 bigint NOT NULL DEFAULT 4000000 CHECK(freight_limit_cm3>=0);
CREATE INDEX freight_prereg_voyage ON freight_prereg(trip_id);
CREATE VIEW loaded_parcel AS
 SELECT p.trip_id,z.reference,z.loaded_at,d.grams,d.length_cm::bigint*d.width_cm*d.height_cm AS cm3,
        f.label AS leg,d.nature, d.recipient_name
 FROM freight_load z JOIN freight_deposit d USING(reference) JOIN freight_prereg p USING(reference)
 JOIN fare f USING(trip_id,origin,destination);
CREATE VIEW freight_space AS
 SELECT t.id,t.freight_limit_g,t.freight_limit_cm3,
        coalesce(sum(l.grams),0)::bigint AS used_g,coalesce(sum(l.cm3),0)::bigint AS used_cm3
 FROM trip t LEFT JOIN loaded_parcel l ON l.trip_id=t.id GROUP BY t.id;

CREATE FUNCTION check_freight_space() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE voyage integer; weight_g integer; volume_cm3 bigint; space freight_space;
BEGIN
 SELECT p.trip_id,d.grams,d.length_cm::bigint*d.width_cm*d.height_cm
 INTO STRICT voyage,weight_g,volume_cm3 FROM freight_prereg p JOIN freight_deposit d USING(reference)
 WHERE p.reference=NEW.reference;
 PERFORM 1 FROM trip WHERE id=voyage AND departed_at IS NULL FOR NO KEY UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Voyage déjà parti.'; END IF;
 SELECT * INTO STRICT space FROM freight_space WHERE id=voyage;
 IF space.used_g+weight_g>space.freight_limit_g THEN
   RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Poids disponible insuffisant dans la soute fret.';
 END IF;
 IF space.used_cm3+volume_cm3>space.freight_limit_cm3 THEN
   RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Volume disponible insuffisant dans la soute fret.';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER enforce_freight_space BEFORE INSERT ON freight_load FOR EACH ROW EXECUTE FUNCTION check_freight_space();

CREATE TABLE freight_manifest(
 trip_id integer PRIMARY KEY REFERENCES trip,number uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
 issued_at timestamptz NOT NULL,agent text NOT NULL,limit_g bigint NOT NULL,limit_cm3 bigint NOT NULL,
 used_g bigint NOT NULL,used_cm3 bigint NOT NULL, parcels jsonb NOT NULL
);
CREATE FUNCTION freeze_freight_manifest() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 INSERT INTO freight_manifest(trip_id,issued_at,agent,limit_g,limit_cm3,used_g,used_cm3,parcels)
 SELECT NEW.trip_id,NEW.issued_at,NEW.agent,s.freight_limit_g,s.freight_limit_cm3,s.used_g,s.used_cm3,
   (SELECT coalesce(jsonb_agg(jsonb_build_object('reference',p.reference,'leg',p.leg,'grams',p.grams,
       'cm3',p.cm3,'nature',p.nature,'recipient',p.recipient_name) ORDER BY p.loaded_at,p.reference),'[]'::jsonb)
     FROM loaded_parcel p WHERE p.trip_id=NEW.trip_id)
 FROM freight_space s WHERE s.id=NEW.trip_id;
 RETURN NEW;
END $$;
CREATE TRIGGER freight_manifest_at_depart AFTER INSERT ON departure_manifest FOR EACH ROW EXECUTE FUNCTION freeze_freight_manifest();
CREATE OR REPLACE FUNCTION move_freight(ref uuid, voyage integer, station integer, action text, operator_name text)
RETURNS timestamptz LANGUAGE plpgsql AS $$
DECLARE route freight_prereg; parcel freight_deposit; departed timestamptz; stamp timestamptz; secret text;
BEGIN
 SELECT * INTO parcel FROM freight_deposit WHERE reference=ref FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Colis déposé introuvable.'; END IF;
 SELECT * INTO STRICT route FROM freight_prereg WHERE reference=ref;
 IF route.trip_id<>voyage OR station<>(CASE WHEN action='charger' THEN route.origin ELSE route.destination END) THEN
   RAISE EXCEPTION USING ERRCODE='P1002',MESSAGE='Voyage ou arrêt incorrect.';
 END IF;
 SELECT departed_at INTO departed FROM trip WHERE id=voyage FOR NO KEY UPDATE;
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

CREATE OR REPLACE FUNCTION schedule_trip(source_id integer, planned timestamptz, coach integer)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE result integer; source trip;
BEGIN
 SELECT * INTO source FROM trip WHERE id=source_id AND template_id IS NULL FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003', MESSAGE='Modèle de voyage inconnu.'; END IF;
 SELECT id INTO result FROM trip WHERE coalesce(template_id,id)=source_id
   AND departure=planned AND coach_no=coach;
 IF FOUND THEN RETURN result; END IF;
 IF planned<=clock_timestamp() THEN RAISE EXCEPTION USING ERRCODE='P1001', MESSAGE='Choisir un départ futur.'; END IF;
 IF NOT EXISTS(SELECT FROM fare WHERE trip_id=source_id) OR NOT EXISTS(SELECT FROM seat WHERE trip_id=source_id)
 OR NOT EXISTS(SELECT FROM trip_stop WHERE trip_id=source_id) THEN
   RAISE EXCEPTION USING ERRCODE='P1001', MESSAGE='Modèle incomplet.';
 END IF;
 INSERT INTO trip(departure,template_id,coach_no,allowance_g,excess_xof_kg,freight_limit_g,freight_limit_cm3)
 VALUES(planned,source_id,coach,source.allowance_g,source.excess_xof_kg,source.freight_limit_g,source.freight_limit_cm3) RETURNING id INTO result;
 INSERT INTO seat SELECT result,number FROM seat WHERE trip_id=source_id;
 INSERT INTO fare SELECT result,origin,destination,label,xof FROM fare WHERE trip_id=source_id;
 INSERT INTO trip_stop SELECT result,position,name FROM trip_stop WHERE trip_id=source_id;
 RETURN result;
END $$;
COMMIT;
