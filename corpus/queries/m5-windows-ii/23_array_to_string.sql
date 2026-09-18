-- D58 and D60: ARRAY_TO_STRING lives in Calcite's BIG_QUERY library, not POSTGRESQL (ADR 0018).
-- libraries: BIG_QUERY
-- expect: has(Read)
SELECT symbol, ARRAY_TO_STRING(tags, '|') AS joined
FROM symbols
ORDER BY symbol
