-- psql variables: company_id, login, enabled. The operator targets a single Company database.
\set ON_ERROR_STOP on
BEGIN;
UPDATE company_admin SET sandbox_refunds=:'enabled'::boolean
WHERE login=:'login' AND EXISTS(SELECT FROM company WHERE id=:'company_id'::uuid AND access_mode='Company')
RETURNING id AS admin_id \gset
COMMIT;
\echo Administrateur : :admin_id
