-- Run in psql as the authorized deployment operator on the target company's database.
-- Required -v arguments: company_id, company_name, entity_name, country, agency_name, template, stop.
\set ON_ERROR_STOP on
SELECT open_company(:'company_id'::uuid, :'company_name', :'entity_name', :'country',
                    :'agency_name', :'template'::integer, :'stop'::integer) AS agency_id;
