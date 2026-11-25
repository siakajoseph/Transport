BEGIN;
CREATE TABLE web_order (
    reference uuid PRIMARY KEY,
    booking_id uuid NOT NULL UNIQUE REFERENCES booking
);

-- Serialize retries for this guest reference before acquiring the shared seat inventory.
CREATE FUNCTION hold_web(ref uuid, t integer, n integer, a integer, b integer, who text)
RETURNS booking LANGUAGE plpgsql AS $$
DECLARE item booking;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(ref::text, 0));
    SELECT bk.* INTO item FROM web_order w JOIN booking bk ON bk.id=w.booking_id
    WHERE w.reference=ref;
    IF FOUND THEN
        IF (item.trip_id,item.number,item.origin,item.destination,item.passenger)
           IS DISTINCT FROM (t,n,a,b,trim(who)) THEN
            RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Cette référence correspond déjà à une autre demande.';
        END IF;
        IF item.state <> 'hold' OR item.expires_at <= clock_timestamp()
           OR EXISTS (SELECT FROM trip WHERE id=t AND departed_at IS NOT NULL) THEN
            RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Cette option est close.';
        END IF;
    ELSE
        SELECT * INTO STRICT item FROM hold_seat(t,n,a,b,who);
        INSERT INTO web_order VALUES(ref,item.id);
    END IF;
    RETURN item;
END $$;
COMMIT;
