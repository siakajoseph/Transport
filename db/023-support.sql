BEGIN;
CREATE TABLE support_diagnostic (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    technician text NOT NULL CHECK (length(btrim(technician)) BETWEEN 1 AND 90),
    reason text NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 240),
    key_hash bytea NOT NULL UNIQUE,
    requested_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    expires_at timestamptz NOT NULL DEFAULT statement_timestamp() + interval '1 hour',
    approved_by uuid REFERENCES company_admin,
    approved_at timestamptz,
    revoked_by uuid REFERENCES company_admin,
    revoked_at timestamptz,
    consulted_at timestamptz,
    CHECK ((approved_by IS NULL) = (approved_at IS NULL)),
    CHECK ((revoked_by IS NULL) = (revoked_at IS NULL)),
    CHECK (consulted_at IS NULL OR (approved_at IS NOT NULL AND revoked_at IS NULL))
);
COMMIT;
