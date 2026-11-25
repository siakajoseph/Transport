\set ON_ERROR_STOP on
BEGIN;
SELECT a.id AS target FROM agency a CROSS JOIN company c
WHERE a.id=:'agency'::uuid AND c.id=:'company'::uuid AND c.access_mode='Company'
FOR UPDATE OF a \gset
-- This command is for the initial desk only. Never recreate or reactivate existing credentials.
SELECT :'target'::uuid AS fresh WHERE NOT EXISTS (
    SELECT FROM counter_desk WHERE agency_id=:'target'::uuid
) \gset
SELECT encode(gen_random_bytes(32),'hex') AS desk_key,encode(gen_random_bytes(32),'hex') AS agent_key \gset
INSERT INTO counter_desk(agency_id,label,credential_hash)
VALUES(:'fresh'::uuid,trim(:'label'),digest(:'desk_key','sha256'));
INSERT INTO staff_account(agency_id,display_name,credential_hash,rights)
VALUES(:'fresh'::uuid,trim(:'name'),digest(:'agent_key','sha256'),ARRAY['vente']);
COMMIT;
SELECT json_build_object('ClePoste',:'desk_key','CleAgent',:'agent_key');
