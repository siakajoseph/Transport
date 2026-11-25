BEGIN;
CREATE TABLE trip_stop (
 trip_id integer REFERENCES trip, position integer CHECK(position>=0),
 name text NOT NULL, PRIMARY KEY(trip_id,position)
);
INSERT INTO trip_stop VALUES(1,0,'Abidjan'),(1,1,'Yamoussoukro'),(1,2,'Bouaké');
CREATE TABLE boarding (
 ticket uuid PRIMARY KEY REFERENCES cash_entry(ticket), agent text NOT NULL,
 scanned_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE FUNCTION board_passenger(t integer, stop integer, ref uuid, proof text, operator_name text)
RETURNS boarding LANGUAGE plpgsql AS $$
DECLARE item record; result boarding;
BEGIN
 SELECT b.trip_id,b.origin,tr.departure INTO item FROM ticket_document d
 JOIN cash_entry e ON e.ticket=d.ticket JOIN booking b ON b.id=e.booking_id
 JOIN trip tr ON tr.id=b.trip_id WHERE d.ticket=ref AND d.token=proof;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1001', MESSAGE='Billet non reconnu.'; END IF;
 IF item.trip_id<>t OR item.origin<>stop THEN
   RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Voyage ou arrêt de montée incorrect.';
 END IF;
 IF item.departure + interval '1 day' <= clock_timestamp() THEN
   RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Contrôle clos pour ce voyage.';
 END IF;
 INSERT INTO boarding(ticket,agent) VALUES(ref,operator_name) ON CONFLICT DO NOTHING RETURNING * INTO result;
 IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='P1002', MESSAGE='Billet déjà utilisé.'; END IF;
 RETURN result;
END $$;
COMMIT;
