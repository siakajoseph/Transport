BEGIN;
ALTER TABLE freight_goods ADD COLUMN revision bigint NOT NULL DEFAULT 1;
CREATE TABLE freight_goods_event (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 label text NOT NULL REFERENCES freight_goods(label),
 admin_id uuid NOT NULL REFERENCES company_admin,
 previous boolean, permitted boolean NOT NULL, revision bigint NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 UNIQUE(label,revision)
);
COMMIT;
