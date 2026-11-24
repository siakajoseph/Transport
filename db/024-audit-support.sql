BEGIN;
ALTER TABLE support_diagnostic
    ADD COLUMN station text CHECK (length(btrim(station)) BETWEEN 1 AND 90),
    ADD COLUMN approved_ip inet,
    ADD COLUMN revoked_ip inet,
    ADD COLUMN consulted_ip inet;
COMMIT;
