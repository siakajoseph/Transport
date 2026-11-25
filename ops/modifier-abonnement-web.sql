-- psql variables: company_id, revision, enabled, reason.
\set ON_ERROR_STOP on
SELECT revise_web_subscription(:'company_id'::uuid, :'revision'::integer, :'enabled'::boolean, :'reason') AS revision;
