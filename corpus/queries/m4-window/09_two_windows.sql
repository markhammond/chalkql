-- expect: count(Window)=2
SELECT symbol, ts,
       SUM(volume) OVER (PARTITION BY symbol ORDER BY ts) AS running_volume,
       COUNT(*) OVER (PARTITION BY ts) AS peer_count
FROM bars_small
ORDER BY symbol, ts
