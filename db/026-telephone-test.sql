BEGIN;
CREATE TABLE sandbox_phone (
    id bigint GENERATED ALWAYS AS IDENTITY (MAXVALUE 999999999999) PRIMARY KEY,
    phone text GENERATED ALWAYS AS ('+999' || lpad(id::text,12,'0')) STORED UNIQUE,
    key_hash bytea NOT NULL UNIQUE CHECK (octet_length(key_hash)=32),
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    expires_at timestamptz NOT NULL DEFAULT statement_timestamp() + interval '24 hours'
);
CREATE INDEX sandbox_phone_expiry ON sandbox_phone(expires_at);
CREATE INDEX freight_pickup_by_phone ON freight_pickup_message(phone);
COMMIT;
