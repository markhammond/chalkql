-- §3.1: an aggregate argument on a masked column counts distinct masks, with no allow-list and
-- no guard -- those belong to AGGREGATE_ONLY alone.
SELECT COUNT(DISTINCT first_name) AS n FROM members
