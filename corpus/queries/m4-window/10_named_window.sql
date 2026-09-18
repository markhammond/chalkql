-- expect: count(Window)=1
-- expect: not(Sort)
SELECT symbol, ts, SUM(volume) OVER w AS running_volume, COUNT(*) OVER w AS n
FROM bars_small
WINDOW w AS (PARTITION BY symbol ORDER BY ts)
