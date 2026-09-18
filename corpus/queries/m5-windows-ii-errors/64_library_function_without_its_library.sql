-- D60: a BigQuery-only function with no `-- libraries:` header does not resolve at all.
-- expect: error=VALIDATION
-- expect: position
SELECT CODE_POINTS_TO_STRING(ARRAY[65, 66])
