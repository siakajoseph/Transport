BEGIN;
CREATE TABLE counter_booking_phone (
    booking_id uuid PRIMARY KEY REFERENCES booking ON DELETE CASCADE,
    phone text NOT NULL CHECK(phone ~ '^[+]?[0-9]{7,15}$')
);
CREATE INDEX counter_booking_phone_lookup ON counter_booking_phone(phone);
COMMIT;
