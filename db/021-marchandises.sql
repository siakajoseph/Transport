BEGIN;
CREATE TABLE freight_goods (
 label text PRIMARY KEY CHECK (label=btrim(label) AND length(label) BETWEEN 1 AND 100),
 permitted boolean NOT NULL
);
ALTER TABLE freight_deposit ADD COLUMN goods_label text REFERENCES freight_goods(label);

CREATE FUNCTION receive_checked_parcel(ref uuid, shift uuid, g integer,l integer,w integer,h integer,
 sender text, phone_s text, recipient text, phone_r text, category text, method text, agreed bigint, goods text)
RETURNS freight_deposit LANGUAGE plpgsql AS $$
DECLARE previous text; result freight_deposit;
BEGIN
 PERFORM 1 FROM freight_prereg WHERE reference=ref FOR UPDATE;
 SELECT goods_label INTO previous FROM freight_deposit WHERE reference=ref;
 IF FOUND THEN
   IF previous IS DISTINCT FROM goods THEN
     RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Marchandise différente du dépôt enregistré.';
   END IF;
 ELSE
   LOCK TABLE freight_goods IN SHARE MODE;
   IF goods IS NULL AND EXISTS(SELECT FROM freight_goods) THEN
     RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Sélectionner une marchandise du catalogue.';
   END IF;
   IF goods IS NOT NULL AND NOT EXISTS(SELECT FROM freight_goods WHERE label=goods AND permitted) THEN
     RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Marchandise interdite ou inconnue. Dépôt refusé.';
   END IF;
 END IF;
 result := receive_parcel(ref,shift,g,l,w,h,sender,phone_s,recipient,phone_r,category,method,agreed);
 IF goods IS NOT NULL AND result.goods_label IS NULL THEN
   UPDATE freight_deposit SET goods_label=goods WHERE reference=ref RETURNING * INTO result;
 END IF;
 RETURN result;
END $$;
COMMIT;
