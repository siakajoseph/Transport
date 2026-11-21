BEGIN;
CREATE TABLE freight_rate (
 trip_id integer NOT NULL, origin integer NOT NULL, destination integer NOT NULL,
 xof_per_kg integer NOT NULL CHECK (xof_per_kg>0), divisor integer NOT NULL CHECK(divisor>0),
 PRIMARY KEY(trip_id,origin,destination), FOREIGN KEY(trip_id,origin,destination) REFERENCES fare
);
-- Rates belong to the template and apply to its scheduled departures.
INSERT INTO freight_rate SELECT trip_id,origin,destination,500,5000 FROM fare
 WHERE trip_id IN (SELECT id FROM trip WHERE template_id IS NULL);
CREATE TABLE freight_prereg (
 request_id uuid PRIMARY KEY, reference uuid UNIQUE NOT NULL DEFAULT gen_random_uuid(),
 trip_id integer NOT NULL REFERENCES trip, origin integer NOT NULL, destination integer NOT NULL,
 grams integer NOT NULL, length_cm integer NOT NULL,width_cm integer NOT NULL,height_cm integer NOT NULL,
 chargeable_kg integer NOT NULL, price_xof bigint NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
COMMIT;
