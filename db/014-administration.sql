BEGIN;
CREATE TABLE company_admin(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), login text NOT NULL UNIQUE,
 salt bytea NOT NULL, password_hash bytea NOT NULL, otp_secret bytea NOT NULL,
 last_step bigint NOT NULL DEFAULT -1, failures integer NOT NULL DEFAULT 0,
 locked_until timestamptz, enabled boolean NOT NULL DEFAULT true
);
CREATE TABLE admin_session(
 token_hash bytea PRIMARY KEY, admin_id uuid NOT NULL REFERENCES company_admin,
 expires_at timestamptz NOT NULL
);
CREATE INDEX admin_session_expiry ON admin_session(expires_at);
ALTER TABLE staff_account ADD COLUMN revision bigint NOT NULL DEFAULT 1;
CREATE FUNCTION advance_staff_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 NEW.revision:=OLD.revision+1;
 RETURN NEW;
END $$;
CREATE TRIGGER staff_revision BEFORE UPDATE ON staff_account FOR EACH ROW EXECUTE FUNCTION advance_staff_revision();

CREATE TABLE staff_admin_event(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 admin_id uuid NOT NULL REFERENCES company_admin, staff_id uuid NOT NULL REFERENCES staff_account,
 previous jsonb, current_state jsonb NOT NULL
);
CREATE FUNCTION staff_summary(s staff_account) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
 SELECT jsonb_build_object('id',s.id,'agency',s.agency_id,'name',s.display_name,'rights',s.rights,
                           'enabled',s.enabled,'revision',s.revision);
$$;
CREATE FUNCTION record_staff_admin_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE actor text := current_setting('transport.admin_id',true);
BEGIN
 IF nullif(actor,'') IS NOT NULL THEN
   INSERT INTO staff_admin_event(admin_id,staff_id,previous,current_state)
   VALUES(actor::uuid,NEW.id,CASE WHEN TG_OP='UPDATE' THEN staff_summary(OLD) END,staff_summary(NEW));
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER staff_admin_change AFTER INSERT OR UPDATE ON staff_account FOR EACH ROW EXECUTE FUNCTION record_staff_admin_change();
COMMIT;
