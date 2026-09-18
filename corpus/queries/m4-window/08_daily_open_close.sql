-- expect: has(Window)
SELECT symbol, ts,
       FIRST_VALUE("open") OVER d AS day_open,
       LAST_VALUE("close") OVER (PARTITION BY symbol, FLOOR(ts TO DAY) ORDER BY ts
                                 ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS day_close
FROM bars_small
WINDOW d AS (PARTITION BY symbol, FLOOR(ts TO DAY) ORDER BY ts)
ORDER BY symbol, ts
