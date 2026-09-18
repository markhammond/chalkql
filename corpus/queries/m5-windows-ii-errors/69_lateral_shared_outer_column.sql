-- ADR 0026: a LATERAL sub-query that constrains two of its own tables with the same outer column.
-- Calcite's general decorrelator keeps only one of the two equalities, so b1 would range over every
-- symbol's bars rather than over s.symbol's. `ts` is deliberately not unique across symbols, so no
-- functional dependency can stand in for the lost equality. D67 refuses it rather than answering
-- wrongly (14-windows-ii.md §8).
-- expect: error=UNSUPPORTED
SELECT s.symbol, x.n
FROM symbols s LEFT JOIN LATERAL (
  SELECT COUNT(*) AS n FROM bars_small b1 JOIN bars_small b2 ON b1.ts = b2.ts
  WHERE b1.symbol = s.symbol AND b2.symbol = s.symbol) x ON TRUE
