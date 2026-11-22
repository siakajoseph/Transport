BEGIN;
ALTER TABLE publisher_mandate ADD COLUMN web_included boolean NOT NULL DEFAULT true;

-- Preserve previously deployed sites when migrating. Revision 0 is the legacy default.
CREATE TABLE web_subscription (
    company_id uuid PRIMARY KEY REFERENCES company(id),
    enabled boolean NOT NULL,
    revision integer NOT NULL CHECK(revision >= 0)
);
INSERT INTO web_subscription SELECT id,true,0 FROM company;
CREATE TABLE web_subscription_event (
    company_id uuid NOT NULL REFERENCES web_subscription,
    revision integer NOT NULL, enabled boolean NOT NULL, recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    recorded_by name NOT NULL DEFAULT session_user, mandate_id uuid REFERENCES publisher_mandate,
    reason text NOT NULL CHECK(length(trim(reason)) BETWEEN 1 AND 200),
    PRIMARY KEY(company_id,revision)
);

CREATE FUNCTION apply_opening_subscription() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE decision boolean; version integer;
BEGIN
    SELECT web_included INTO STRICT decision FROM publisher_mandate WHERE id=NEW.mandate_id;
    UPDATE web_subscription SET enabled=decision,revision=revision+1 WHERE company_id=NEW.company_id
    RETURNING revision INTO STRICT version;
    INSERT INTO web_subscription_event(company_id,revision,enabled,mandate_id,reason)
    VALUES(NEW.company_id,version,decision,NEW.mandate_id,'Ouverture par mandat');
    RETURN NEW;
END $$;
CREATE TRIGGER apply_opening_subscription AFTER UPDATE OF mandate_id ON company_opening
FOR EACH ROW WHEN (OLD.mandate_id IS NULL AND NEW.mandate_id IS NOT NULL)
EXECUTE FUNCTION apply_opening_subscription();

CREATE FUNCTION revise_web_subscription(target uuid, expected integer, decision boolean, explanation text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE current_state web_subscription;
BEGIN
    SELECT w.* INTO current_state FROM web_subscription w JOIN company_opening o USING(company_id)
        WHERE w.company_id=target FOR UPDATE OF w;
    IF NOT FOUND THEN RAISE EXCEPTION 'Compagnie ouverte introuvable.'; END IF;
    IF current_state.revision IS DISTINCT FROM expected THEN
        RAISE EXCEPTION 'Révision périmée. Relire la souscription.';
    END IF;
    IF decision IS NULL OR coalesce(length(trim(explanation)),0) NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION 'Décision et motif requis.';
    END IF;
    IF current_state.enabled=decision THEN RETURN current_state.revision; END IF;
    UPDATE web_subscription SET enabled=decision, revision=revision+1 WHERE company_id=target
        RETURNING revision INTO current_state.revision;
    INSERT INTO web_subscription_event(company_id,revision,enabled,reason)
        VALUES(target,current_state.revision,decision,trim(explanation));
    RETURN current_state.revision;
END $$;
REVOKE ALL ON FUNCTION revise_web_subscription(uuid,integer,boolean,text) FROM PUBLIC;

-- A denied insertion rolls back the seat hold in the same transaction. Existing orders
-- bypass this trigger, including retries and settlements already in progress.
CREATE FUNCTION require_web_subscription() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM 1 FROM web_subscription WHERE enabled FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE='P1011', MESSAGE='Les nouvelles réservations web sont fermées pour cette compagnie.';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER require_web_subscription BEFORE INSERT ON web_order
FOR EACH ROW EXECUTE FUNCTION require_web_subscription();

CREATE VIEW publisher_catalog_entry AS
SELECT c.id AS company_id,c.name AS company_name,o.opened_at,w.enabled AS web_enabled,w.revision
FROM company c JOIN company_opening o ON o.company_id=c.id
JOIN web_subscription w ON w.company_id=c.id;
COMMIT;
