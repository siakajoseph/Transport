BEGIN;
CREATE SEQUENCE trip_number OWNED BY trip.id;
SELECT setval('trip_number',coalesce(max(id),0)+1,false) FROM trip;
ALTER TABLE trip ALTER COLUMN id SET DEFAULT nextval('trip_number');
ALTER TABLE trip ADD COLUMN template_id integer REFERENCES trip,
 ADD COLUMN coach_no integer NOT NULL DEFAULT 1 CHECK(coach_no BETWEEN 1 AND 99);
CREATE UNIQUE INDEX trip_schedule ON trip(coalesce(template_id,id),departure,coach_no);

CREATE FUNCTION schedule_trip(source_id integer, planned timestamptz, coach integer)
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
 INSERT INTO trip(departure,template_id,coach_no,allowance_g,excess_xof_kg)
 VALUES(planned,source_id,coach,source.allowance_g,source.excess_xof_kg) RETURNING id INTO result;
 INSERT INTO seat SELECT result,number FROM seat WHERE trip_id=source_id;
 INSERT INTO fare SELECT result,origin,destination,label,xof FROM fare WHERE trip_id=source_id;
 INSERT INTO trip_stop SELECT result,position,name FROM trip_stop WHERE trip_id=source_id;
 RETURN result;
END $$;
COMMIT;
