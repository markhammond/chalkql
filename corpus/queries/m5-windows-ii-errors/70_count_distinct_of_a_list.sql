-- D58: a LIST has no equality, so nothing de-duplicates one. COUNT(DISTINCT) over a LIST column
-- used to count every list as one (F128); the planner refuses it by name, naming the column, before
-- the executor's own refusal at prepare.
-- expect: error=UNSUPPORTED
SELECT COUNT(DISTINCT tags) FROM symbols
