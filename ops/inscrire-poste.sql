-- psql parameters: agency (UUID), label (workstation name). The key is displayed once.
\set ON_ERROR_STOP on
BEGIN;
SELECT encode(gen_random_bytes(32),'hex') AS new_key \gset
INSERT INTO counter_desk(agency_id,label,credential_hash)
SELECT :'agency'::uuid, :'label', digest(:'new_key','sha256') FROM company WHERE access_mode='Company'
RETURNING id AS desk_id \gset
COMMIT;
\echo Poste : :desk_id
\echo Clé : :new_key
