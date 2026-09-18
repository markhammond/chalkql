-- D60: STRING_AGG lives in Calcite's POSTGRESQL library, and Calcite maps it onto LISTAGG during
-- validation (ADR 0018). Without the header this is a VALIDATION error — the negative corpus has it.
-- libraries: POSTGRESQL
-- expect: has(HashAggregate)
SELECT quote, STRING_AGG(base, ', ' ORDER BY base) AS bases
FROM symbols
GROUP BY quote
ORDER BY quote
