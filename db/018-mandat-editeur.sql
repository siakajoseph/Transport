BEGIN;
CREATE TABLE publisher_mandate (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id uuid NOT NULL REFERENCES company(id),
    operator_name text NOT NULL CHECK (length(trim(operator_name)) BETWEEN 1 AND 100),
    key_hash bytea NOT NULL UNIQUE CHECK (octet_length(key_hash)=32),
    issued_by name NOT NULL DEFAULT session_user,
    expires_at timestamptz NOT NULL DEFAULT (clock_timestamp() + interval '20 minutes'),
    revoked_at timestamptz
);
ALTER TABLE company_opening ADD COLUMN mandate_id uuid UNIQUE REFERENCES publisher_mandate;

CREATE FUNCTION publisher_open(mandate uuid, company_name text, entity_name text,
    country_code text, agency_name text, template integer, station integer)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE target uuid; agency_key uuid;
BEGIN
    SELECT company_id INTO target FROM publisher_mandate WHERE id=mandate
        AND revoked_at IS NULL AND expires_at > clock_timestamp() FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1010', MESSAGE='Mandat invalide.'; END IF;
    PERFORM 1 FROM company WHERE id=target AND access_mode='Company' FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Profil incorrect.'; END IF;
    IF EXISTS(SELECT FROM company_opening WHERE company_id=target AND mandate_id IS DISTINCT FROM mandate) THEN
        RAISE EXCEPTION 'Cette compagnie a déjà été ouverte.';
    END IF;
    agency_key := open_company(target,company_name,entity_name,country_code,agency_name,template,station);
    UPDATE company_opening SET mandate_id=mandate WHERE company_id=target AND mandate_id IS NULL;
    RETURN agency_key;
END $$;
REVOKE ALL ON FUNCTION publisher_open(uuid,text,text,text,text,integer,integer) FROM PUBLIC;
COMMIT;
