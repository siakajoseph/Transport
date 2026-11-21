BEGIN;
-- One operational database and API per company. The deployment profile is pinned on first startup.
CREATE TABLE company (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(), name text NOT NULL,
 access_mode text CHECK(access_mode IN ('Demo','Company'))
);
INSERT INTO company(name) VALUES ('Compagnie de démonstration');
CREATE TABLE legal_entity(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),name text NOT NULL,country char(2) NOT NULL);
CREATE TABLE agency(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),entity_id uuid NOT NULL REFERENCES legal_entity,name text NOT NULL);
CREATE TABLE agency_origin(
 template_id integer NOT NULL,position integer NOT NULL,agency_id uuid NOT NULL REFERENCES agency,
 PRIMARY KEY(template_id,position,agency_id),FOREIGN KEY(template_id,position) REFERENCES trip_stop
);
CREATE TABLE counter_desk(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),agency_id uuid NOT NULL REFERENCES agency,
 label text NOT NULL,credential_hash bytea UNIQUE NOT NULL CHECK(octet_length(credential_hash)=32),
 enabled boolean NOT NULL DEFAULT true
);
ALTER TABLE booking ADD COLUMN desk_id uuid REFERENCES counter_desk;
ALTER TABLE cash_session ADD COLUMN desk_id uuid REFERENCES counter_desk;
CREATE INDEX cash_session_desk ON cash_session(desk_id,opened_at) WHERE desk_id IS NOT NULL;

CREATE FUNCTION desk_hold(desk uuid,t integer,n integer,a integer,b integer,who text)
RETURNS booking LANGUAGE plpgsql AS $$
DECLARE result booking;
BEGIN
 PERFORM 1 FROM counter_desk d JOIN agency_origin o USING(agency_id)
 JOIN trip v ON coalesce(v.template_id,v.id)=o.template_id
 WHERE d.id=desk AND d.enabled AND v.id=t AND o.position=a FOR SHARE OF d,o;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Point de vente indisponible.'; END IF;
 SELECT * INTO STRICT result FROM hold_seat(t,n,a,b,who);
 UPDATE booking SET desk_id=desk WHERE id=result.id RETURNING * INTO result;
 RETURN result;
END $$;
CREATE FUNCTION desk_collect(desk uuid,option_id uuid,shift uuid)
RETURNS cash_entry LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT FROM booking WHERE id=option_id AND desk_id=desk)
 OR NOT EXISTS(SELECT FROM cash_session WHERE id=shift AND desk_id=desk) THEN
   RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Option ou caisse introuvable.';
 END IF;
 RETURN collect_cash(option_id,shift);
END $$;
CREATE FUNCTION desk_close(desk uuid,shift uuid,amount integer)
RETURNS cash_session LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT FROM cash_session WHERE id=shift AND desk_id=desk) THEN
   RAISE EXCEPTION USING ERRCODE='P1003',MESSAGE='Caisse introuvable.';
 END IF;
 RETURN close_shift(shift,amount);
END $$;
COMMIT;
