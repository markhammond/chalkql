-- D58: v1 lists are exactly one level deep, so ARRAY_AGG over a LIST column would produce a
-- LIST<LIST>, which the type mapper refuses. Calcite validates it happily — the refusal is Chalk's.
-- libraries: POSTGRESQL
-- expect: error=UNSUPPORTED
SELECT ARRAY_AGG(tags) FROM symbols
