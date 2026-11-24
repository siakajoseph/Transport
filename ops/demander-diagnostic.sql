\set ON_ERROR_STOP on
\if :{?minutes}
\else
\set minutes 60
\endif
BEGIN;
SELECT encode(gen_random_bytes(32),'hex') AS support_key \gset
INSERT INTO support_diagnostic(technician,reason,key_hash,station,expires_at)
SELECT btrim(:'technician'),btrim(:'reason'),digest(:'support_key','sha256'),btrim(:'station'),
       statement_timestamp()+make_interval(mins => :'minutes'::integer)
FROM company WHERE id=:'company_id'::uuid AND access_mode='Company'
  AND :'minutes'::integer BETWEEN 1 AND 60 AND length(btrim(:'station')) BETWEEN 1 AND 90 RETURNING id AS support_id \gset
COMMIT;
\echo Demande : :support_id
\echo Clé : :support_key
