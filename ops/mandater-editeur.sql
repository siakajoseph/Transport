-- psql -v company_id=<UUID> -v operator_name=<nom du destinataire> -f ...
\set ON_ERROR_STOP on
BEGIN;
SELECT encode(gen_random_bytes(32),'hex') AS secret \gset
INSERT INTO publisher_mandate(company_id,operator_name,key_hash)
SELECT id, trim(:'operator_name'), digest(:'secret','sha256') FROM company
WHERE id=:'company_id'::uuid AND access_mode='Company'
  AND NOT EXISTS(SELECT FROM company_opening)
RETURNING id AS mandate_id, expires_at AS expiry \gset
COMMIT;
\echo Mandat : :mandate_id
\echo Expiration : :expiry
\echo Clé : :secret
