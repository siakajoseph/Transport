\set ON_ERROR_STOP on
BEGIN;
SELECT encode(gen_random_bytes(32),'hex') AS support_key \gset
INSERT INTO support_diagnostic(technician,reason,key_hash)
SELECT btrim(:'technician'),btrim(:'reason'),digest(:'support_key','sha256')
FROM company WHERE access_mode='Company' RETURNING id AS support_id \gset
COMMIT;
\echo Demande : :support_id
\echo Clé : :support_key
