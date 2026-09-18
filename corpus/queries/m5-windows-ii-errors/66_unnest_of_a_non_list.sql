-- D66: Calcite refuses this itself.
-- expect: error=VALIDATION
-- expect: position
SELECT * FROM (VALUES (1)) AS t(a), UNNEST(t.a) AS u(x)
