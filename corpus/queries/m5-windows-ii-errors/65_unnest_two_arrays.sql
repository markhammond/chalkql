-- D66: PostgreSQL's zip form. The rule takes one list per node, so the correlate survives and is
-- reported as UNSUPPORTED naming its shape.
-- expect: error=UNSUPPORTED
SELECT * FROM (VALUES (ARRAY[1, 2], ARRAY[3, 4])) AS t(a, b), UNNEST(t.a, t.b) AS u(x, y)
