BEGIN;
CREATE VIEW freight_tracking AS
 SELECT p.reference,
    CASE WHEN h.reference IS NOT NULL THEN 'Livré'
         WHEN a.reference IS NOT NULL THEN 'En attente de retrait'
         WHEN l.reference IS NOT NULL AND t.departed_at IS NOT NULL THEN 'En transit'
         WHEN l.reference IS NOT NULL THEN 'Chargé'
         WHEN d.reference IS NOT NULL THEN 'Déposé'
         ELSE 'Préenregistré' END AS state,
    CASE WHEN a.reference IS NOT NULL THEN dest.name
         WHEN l.reference IS NOT NULL AND t.departed_at IS NOT NULL THEN orig.name || ' → ' || dest.name
         WHEN d.reference IS NOT NULL THEN orig.name END AS area
 FROM freight_prereg p JOIN trip t ON t.id=p.trip_id
 LEFT JOIN trip_stop orig ON orig.trip_id=p.trip_id AND orig.position=p.origin
 LEFT JOIN trip_stop dest ON dest.trip_id=p.trip_id AND dest.position=p.destination
 LEFT JOIN freight_deposit d USING(reference) LEFT JOIN freight_load l USING(reference)
 LEFT JOIN freight_arrival a USING(reference) LEFT JOIN freight_handover h USING(reference);
COMMIT;
