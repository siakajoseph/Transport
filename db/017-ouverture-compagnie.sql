BEGIN;
CREATE TABLE company_opening (
    company_id uuid PRIMARY KEY REFERENCES company(id),
    entity_id uuid NOT NULL REFERENCES legal_entity,
    agency_id uuid NOT NULL REFERENCES agency,
    parameters jsonb NOT NULL,
    opened_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    opened_by name NOT NULL DEFAULT session_user
);

CREATE FUNCTION open_company(target uuid, company_name text, entity_name text,
    country_code text, agency_name text, template integer, station integer)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE previous company_opening; request jsonb; new_entity uuid; new_agency uuid;
BEGIN
    PERFORM 1 FROM company WHERE id=target AND access_mode='Company' FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Compagnie ou profil incorrect.'; END IF;
    company_name := trim(company_name);
    entity_name := trim(entity_name);
    agency_name := trim(agency_name);
    country_code := upper(trim(country_code));
    IF coalesce(length(company_name),0) NOT BETWEEN 1 AND 120
       OR coalesce(length(entity_name),0) NOT BETWEEN 1 AND 120
       OR coalesce(length(agency_name),0) NOT BETWEEN 1 AND 120
       OR coalesce(country_code,'') !~ '^[A-Z]{2}$' THEN
        RAISE EXCEPTION 'Nom ou code pays invalide.';
    END IF;
    request := jsonb_build_object('company',company_name,'entity',entity_name,'country',country_code,
        'agency',agency_name,'template',template,'station',station);
    SELECT * INTO previous FROM company_opening WHERE company_id=target;
    IF FOUND THEN
        IF previous.parameters <> request THEN RAISE EXCEPTION 'Ouverture déjà enregistrée avec des paramètres différents.'; END IF;
        RETURN previous.agency_id;
    END IF;
    -- Only bootstrap a dedicated database before its first operational activity.
    LOCK TABLE legal_entity, booking, freight_prereg, cash_session, company_admin IN SHARE ROW EXCLUSIVE MODE;
    IF EXISTS(SELECT FROM legal_entity) OR EXISTS(SELECT FROM booking)
        OR EXISTS(SELECT FROM freight_prereg) OR EXISTS(SELECT FROM cash_session)
        OR EXISTS(SELECT FROM company_admin) THEN
        RAISE EXCEPTION 'Base déjà en exploitation.';
    END IF;
    IF NOT EXISTS(SELECT FROM trip_stop s JOIN trip t ON t.id=s.trip_id
        WHERE t.id=template AND t.template_id IS NULL AND s.position=station) THEN
        RAISE EXCEPTION 'Arrêt du modèle de voyage inconnu.';
    END IF;
    UPDATE company SET name=company_name WHERE id=target;
    INSERT INTO legal_entity(name,country) VALUES(entity_name,country_code) RETURNING id INTO new_entity;
    INSERT INTO agency(entity_id,name) VALUES(new_entity,agency_name) RETURNING id INTO new_agency;
    INSERT INTO agency_origin(template_id,position,agency_id) VALUES(template,station,new_agency);
    INSERT INTO company_opening(company_id,entity_id,agency_id,parameters)
    VALUES(target,new_entity,new_agency,request);
    RETURN new_agency;
END $$;
REVOKE ALL ON FUNCTION open_company(uuid,text,text,text,text,integer,integer) FROM PUBLIC;
COMMIT;
