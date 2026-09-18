-- §5 corpus 09. An exact COUNT(DISTINCT …) is pushed; the approximate form is never pushed without
-- the profile's permission, which ApproximationTests checks without needing a second database.
-- expect: has(RemoteQuery)
SELECT COUNT(DISTINCT l_suppkey) AS suppliers
FROM duck.lineitem
