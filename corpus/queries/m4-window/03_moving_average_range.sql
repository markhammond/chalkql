-- expect: has(Window)
-- expect: not(Sort)
-- expect: has(IndexLookup)
SELECT symbol, ts,
       AVG("close") OVER (PARTITION BY symbol ORDER BY ts RANGE INTERVAL '1' HOUR PRECEDING) AS avg1h
FROM bars_small
