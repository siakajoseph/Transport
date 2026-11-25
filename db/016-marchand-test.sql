BEGIN;
-- The existing ticket identity is consumed by boarding, manifests and bag registration.
ALTER TABLE cash_entry ADD COLUMN channel text NOT NULL DEFAULT 'cash'
    CHECK (channel IN ('cash','sandbox'));
ALTER TABLE cash_entry ADD CHECK (channel = 'cash' OR session_id IS NULL);

-- Each isolated database represents one company. The merchant here is its sandbox account.
CREATE TABLE sandbox_receipt (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    reference uuid NOT NULL UNIQUE REFERENCES web_order,
    merchant uuid NOT NULL REFERENCES company(id),
    amount integer NOT NULL CHECK (amount >= 0),
    currency text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    outcome text NOT NULL DEFAULT 'received' CHECK (outcome IN ('received','issued','review')),
    ticket uuid UNIQUE REFERENCES cash_entry,
    CHECK ((outcome='issued') = (ticket IS NOT NULL))
);

CREATE FUNCTION sandbox_charge(ref uuid) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE order_row record; receipt uuid;
BEGIN
    SELECT b.*,c.id AS merchant_id INTO order_row FROM web_order w
    JOIN booking b ON b.id=w.booking_id CROSS JOIN company c
    WHERE w.reference=ref FOR UPDATE OF w;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003', MESSAGE='Option inconnue.'; END IF;
    SELECT id INTO receipt FROM sandbox_receipt WHERE reference=ref;
    IF FOUND THEN RETURN receipt; END IF;
    IF order_row.state <> 'hold' OR order_row.expires_at <= clock_timestamp()
       OR EXISTS (SELECT FROM trip WHERE id=order_row.trip_id AND departed_at IS NOT NULL) THEN
        RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Option close avant le paiement.';
    END IF;
    INSERT INTO sandbox_receipt(reference,merchant,amount,currency)
    VALUES(ref,order_row.merchant_id,order_row.xof,'XOF') RETURNING id INTO receipt;
    RETURN receipt;
END $$;

CREATE FUNCTION reconcile_sandbox(ref uuid, payment uuid) RETURNS sandbox_receipt LANGUAGE plpgsql AS $$
DECLARE receipt sandbox_receipt; item booking; issued_ticket uuid; open_trip boolean;
BEGIN
    SELECT * INTO receipt FROM sandbox_receipt WHERE id=payment AND reference=ref FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003', MESSAGE='Paiement inconnu.'; END IF;
    IF receipt.outcome <> 'received' THEN RETURN receipt; END IF;
    SELECT b.* INTO STRICT item FROM web_order w JOIN booking b ON b.id=w.booking_id WHERE w.reference=ref;
    PERFORM 1 FROM seat WHERE trip_id=item.trip_id AND number=item.number FOR UPDATE;
    SELECT * INTO STRICT item FROM booking WHERE id=item.id FOR UPDATE;
    SELECT departed_at IS NULL INTO open_trip FROM trip WHERE id=item.trip_id FOR SHARE;
    IF item.state='hold' AND open_trip AND item.expires_at > clock_timestamp()
        AND receipt.amount=item.xof AND receipt.currency='XOF'
        AND EXISTS (SELECT FROM company WHERE id=receipt.merchant) THEN
        UPDATE booking SET state='sold' WHERE id=item.id;
        INSERT INTO cash_entry(booking_id,xof,channel) VALUES(item.id,item.xof,'sandbox')
        RETURNING ticket INTO issued_ticket;
    END IF;
    UPDATE sandbox_receipt SET ticket=issued_ticket,
        outcome=CASE WHEN issued_ticket IS NULL THEN 'review' ELSE 'issued' END
    WHERE id=payment RETURNING * INTO receipt;
    RETURN receipt;
END $$;
COMMIT;
