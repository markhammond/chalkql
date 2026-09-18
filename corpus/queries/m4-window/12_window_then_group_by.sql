-- expect: has(Window)
-- expect: has(HashAggregate)
SELECT symbol, MAX(running_volume) AS peak
FROM (SELECT b.symbol AS symbol,
             SUM(b.volume) OVER (PARTITION BY b.symbol ORDER BY b.ts) AS running_volume
      FROM bars_small b JOIN symbols s ON b.symbol = s.symbol)
GROUP BY symbol
ORDER BY symbol
