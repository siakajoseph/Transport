BEGIN;
ALTER TABLE company_admin ADD COLUMN support_only boolean NOT NULL DEFAULT false;
ALTER TABLE support_diagnostic ADD COLUMN technician_id uuid REFERENCES company_admin;
COMMIT;
