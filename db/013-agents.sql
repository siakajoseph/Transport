BEGIN;
CREATE TABLE staff_account(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),agency_id uuid NOT NULL REFERENCES agency,
 display_name text NOT NULL CHECK(length(trim(display_name)) BETWEEN 1 AND 90),
 credential_hash bytea UNIQUE NOT NULL CHECK(octet_length(credential_hash)=32),
 enabled boolean NOT NULL DEFAULT true,
 rights text[] NOT NULL CHECK(cardinality(rights)>0 AND array_position(rights,NULL) IS NULL AND
     rights <@ ARRAY['vente','fret','embarquement','depart']::text[])
);
-- Historical entries retain their original desk attribution.
ALTER TABLE booking ADD COLUMN staff_id uuid REFERENCES staff_account;
ALTER TABLE cash_session ADD COLUMN staff_id uuid REFERENCES staff_account;
DO $$ BEGIN
 IF EXISTS(SELECT FROM cash_session WHERE desk_id IS NOT NULL AND closed_at IS NULL)
 OR EXISTS(SELECT FROM booking WHERE desk_id IS NOT NULL AND state='hold' AND expires_at>now()) THEN
   RAISE EXCEPTION 'Terminer les options actives et clôturer les caisses des postes v0.11 avant la migration.';
 END IF;
END $$;

CREATE FUNCTION staff_hold(staff uuid,desk uuid,t integer,n integer,a integer,b integer,who text)
RETURNS booking LANGUAGE plpgsql AS $$
DECLARE result booking;
BEGIN
 SELECT * INTO STRICT result FROM desk_hold(desk,t,n,a,b,who);
 UPDATE booking SET staff_id=staff WHERE id=result.id RETURNING * INTO result;
 RETURN result;
END $$;

CREATE FUNCTION own_shift(shift uuid,desk uuid,staff uuid) RETURNS uuid LANGUAGE plpgsql STABLE AS $$
BEGIN
 IF desk IS NOT NULL AND NOT EXISTS(SELECT FROM cash_session s WHERE s.id=shift AND s.desk_id=desk AND s.staff_id=staff) THEN
   RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Caisse introuvable.';
 END IF;
 RETURN shift;
END $$;
CREATE FUNCTION staff_collect(staff uuid,desk uuid,option_id uuid,shift uuid)
RETURNS cash_entry LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT FROM booking WHERE id=option_id AND desk_id=desk AND staff_id=staff) THEN
   RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Option introuvable.';
 END IF;
 RETURN collect_cash(option_id,own_shift(shift,desk,staff));
END $$;

CREATE FUNCTION serves_stop(desk uuid,t integer,station integer) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT desk IS NULL OR EXISTS(SELECT FROM counter_desk d JOIN agency_origin o USING(agency_id)
     JOIN trip v ON coalesce(v.template_id,v.id)=o.template_id
     WHERE d.id=desk AND d.enabled AND v.id=t AND o.position=station);
$$;
CREATE FUNCTION checked_stop(desk uuid,t integer,station integer) RETURNS integer LANGUAGE plpgsql STABLE AS $$
BEGIN
 IF NOT serves_stop(desk,t,station) THEN
   RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Arrêt hors du périmètre du poste.';
 END IF;
 RETURN station;
END $$;
CREATE FUNCTION checked_departure(desk uuid,t integer) RETURNS integer LANGUAGE plpgsql STABLE AS $$
BEGIN
 PERFORM checked_stop(desk,t,(SELECT min(position) FROM trip_stop WHERE trip_id=t));
 RETURN t;
END $$;
CREATE FUNCTION checked_parcel(desk uuid,ref uuid,at_origin boolean) RETURNS uuid LANGUAGE plpgsql STABLE AS $$
BEGIN
 IF desk IS NOT NULL AND NOT EXISTS(SELECT FROM freight_prereg p WHERE p.reference=ref AND
     serves_stop(desk,p.trip_id,CASE WHEN at_origin THEN p.origin ELSE p.destination END)) THEN
   RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Colis introuvable dans cette agence.';
 END IF;
 RETURN ref;
END $$;
COMMIT;
