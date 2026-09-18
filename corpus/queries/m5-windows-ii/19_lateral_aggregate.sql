-- D67: a correlated aggregate becomes a grouped join. Written as LEFT JOIN LATERAL … ON TRUE
-- because the inner form is the one shape Calcite 1.42 cannot decorrelate (V26, ADR 0018).
-- expect: not(Correlate)
SELECT s.symbol, x.avg_close
FROM symbols s
LEFT JOIN LATERAL (SELECT AVG(b."close") AS avg_close FROM bars_small b
                   WHERE b.symbol = s.symbol) x ON TRUE
ORDER BY s.symbol
