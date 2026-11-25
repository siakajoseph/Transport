-- One demo company per database. Stop positions are ranks on this particular trip.
BEGIN;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE TABLE trip (
    id integer PRIMARY KEY,
    departure timestamptz NOT NULL
);
CREATE TABLE seat (
    trip_id integer REFERENCES trip,
    number integer CHECK (number > 0),
    PRIMARY KEY (trip_id, number)
);
CREATE TABLE fare (
    trip_id integer REFERENCES trip,
    origin integer NOT NULL,
    destination integer NOT NULL CHECK (destination > origin),
    label text NOT NULL,
    xof integer NOT NULL CHECK (xof >= 0),
    PRIMARY KEY (trip_id, origin, destination)
);
CREATE TABLE booking (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    trip_id integer NOT NULL,
    number integer NOT NULL,
    origin integer NOT NULL,
    destination integer NOT NULL,
    xof integer NOT NULL CHECK (xof >= 0),
    passenger text NOT NULL CHECK (length(trim(passenger)) BETWEEN 1 AND 100),
    expires_at timestamptz NOT NULL,
    state text NOT NULL CHECK (state IN ('hold','sold','expired')),
    FOREIGN KEY (trip_id,number) REFERENCES seat,
    FOREIGN KEY (trip_id,origin,destination) REFERENCES fare,
    EXCLUDE USING gist (trip_id WITH =, number WITH =,
        int4range(origin,destination,'[)') WITH &&) WHERE (state IN ('hold','sold'))
);
-- The same booking can produce exactly one cash entry and ticket reference.
CREATE TABLE cash_entry (
    ticket uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL UNIQUE REFERENCES booking,
    xof integer NOT NULL CHECK (xof >= 0),
    paid_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
INSERT INTO trip VALUES (1, date_trunc('day', now()) + interval '7 days 8 hours');
INSERT INTO seat SELECT 1, generate_series(1,32);
INSERT INTO fare VALUES (1,0,1,'Abidjan → Yamoussoukro',4000),
    (1,1,2,'Yamoussoukro → Bouaké',2500), (1,0,2,'Abidjan → Bouaké',6000);

CREATE FUNCTION hold_seat(t integer, n integer, a integer, b integer, who text)
RETURNS booking LANGUAGE plpgsql AS $$
DECLARE price integer; cutoff timestamptz; result booking;
BEGIN
    PERFORM 1 FROM seat WHERE trip_id=t AND number=n FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1001', MESSAGE='Siège inconnu.'; END IF;
    SELECT fare.xof,trip.departure INTO price,cutoff
        FROM fare JOIN trip ON trip.id=fare.trip_id
        WHERE fare.trip_id=t AND fare.origin=a AND fare.destination=b;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1001', MESSAGE='Tronçon inconnu.'; END IF;
    IF cutoff <= clock_timestamp() THEN RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Départ clos.'; END IF;
    UPDATE booking SET state='expired' WHERE trip_id=t AND number=n
        AND state='hold' AND expires_at <= clock_timestamp();
    INSERT INTO booking(trip_id,number,origin,destination,xof,passenger,expires_at,state)
        VALUES(t,n,a,b,price,trim(who),least(clock_timestamp()+interval '8 minutes',cutoff),'hold')
        RETURNING * INTO result;
    RETURN result;
END $$;

CREATE FUNCTION confirm_cash(key uuid) RETURNS cash_entry LANGUAGE plpgsql AS $$
DECLARE item booking; result cash_entry;
BEGIN
    -- Same lock order as hold_seat, including when the option crosses its deadline.
    PERFORM 1 FROM seat s JOIN booking b ON (s.trip_id,s.number)=(b.trip_id,b.number)
        WHERE b.id=key FOR UPDATE OF s;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003', MESSAGE='Option inconnue.'; END IF;
    SELECT * INTO STRICT item FROM booking WHERE id=key FOR UPDATE;
    IF item.state='sold' THEN
        SELECT * INTO STRICT result FROM cash_entry WHERE booking_id=key;
        RETURN result;
    END IF;
    IF item.state <> 'hold' OR item.expires_at <= clock_timestamp() THEN
        RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Option expirée.';
    END IF;
    UPDATE booking SET state='sold' WHERE id=key;
    INSERT INTO cash_entry(booking_id,xof) VALUES(key,item.xof) RETURNING * INTO result;
    RETURN result;
END $$;
COMMIT;
