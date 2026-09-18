-- §6 corpus 04. Three sources in one statement: the POCO dimension, DuckDB's bars and SQLite's
-- funding rates. Rev 3's exit criterion is that this plan's digest is the same across a hundred
-- plans and across a sidecar restart.
-- expect: has(RemoteQuery)
-- expect: count(RemoteQuery)=2
SELECT s.symbol, COUNT(*) AS bar_count, MIN(f.rate) AS min_rate
FROM symbols s
JOIN duck.bars b ON b.symbol = s.symbol
JOIN sqlite.funding f ON f.symbol = s.symbol
WHERE b.ts < TIMESTAMP '2026-01-01 00:05:00'
GROUP BY s.symbol
ORDER BY s.symbol
