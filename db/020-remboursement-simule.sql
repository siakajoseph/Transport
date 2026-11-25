BEGIN;
ALTER TABLE company_admin ADD COLUMN sandbox_refunds boolean NOT NULL DEFAULT false;
CREATE TABLE sandbox_refund (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_id uuid NOT NULL UNIQUE REFERENCES sandbox_receipt,
    amount integer NOT NULL CHECK(amount >= 0),
    currency text NOT NULL,
    admin_id uuid NOT NULL REFERENCES company_admin,
    reason text NOT NULL CHECK(length(trim(reason)) BETWEEN 1 AND 200),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE FUNCTION refund_sandbox(payment uuid, actor uuid, explanation text) RETURNS sandbox_refund
LANGUAGE plpgsql AS $$
DECLARE receipt sandbox_receipt; refund sandbox_refund; item booking;
BEGIN
    PERFORM 1 FROM company_admin WHERE id=actor AND enabled AND sandbox_refunds FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1012', MESSAGE='Habilitation requise.'; END IF;
    explanation := trim(explanation);
    IF coalesce(length(explanation),0) NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION USING ERRCODE='P1001', MESSAGE='Indiquer un motif de 1 à 200 caractères.';
    END IF;
    SELECT * INTO receipt FROM sandbox_receipt WHERE id=payment FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1003', MESSAGE='Paiement inconnu.'; END IF;
    IF receipt.outcome <> 'review' THEN RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Paiement hors rapprochement.'; END IF;
    SELECT * INTO refund FROM sandbox_refund WHERE payment_id=payment;
    IF FOUND THEN
        IF refund.admin_id=actor AND refund.reason=explanation THEN RETURN refund; END IF;
        RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Ce remboursement a déjà été enregistré.';
    END IF;
    SELECT b.* INTO STRICT item FROM web_order w JOIN booking b ON b.id=w.booking_id WHERE w.reference=receipt.reference;
    PERFORM 1 FROM seat WHERE trip_id=item.trip_id AND number=item.number FOR UPDATE;
    UPDATE booking SET state='expired' WHERE id=item.id AND state='hold';
    INSERT INTO sandbox_refund(payment_id,amount,currency,admin_id,reason)
    VALUES(payment,receipt.amount,receipt.currency,actor,explanation) RETURNING * INTO refund;
    RETURN refund;
END $$;
REVOKE ALL ON FUNCTION refund_sandbox(uuid,uuid,text) FROM PUBLIC;
COMMIT;
