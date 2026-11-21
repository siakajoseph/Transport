-- psql parameters: agency UUID, name, rights (comma-separated).
\set ON_ERROR_STOP on
BEGIN;
SELECT encode(gen_random_bytes(32),'hex') AS personal_key \gset
INSERT INTO staff_account(agency_id,display_name,rights,credential_hash)
SELECT :'agency'::uuid, :'name', string_to_array(:'rights',','), digest(:'personal_key','sha256')
FROM company WHERE access_mode='Company' RETURNING id AS staff_id \gset
COMMIT;
\echo Agent : :staff_id
\echo Clé : :personal_key
