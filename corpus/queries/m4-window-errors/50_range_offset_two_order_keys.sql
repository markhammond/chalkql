-- expect: error=VALIDATION
-- expect: position
SELECT symbol, AVG("close") OVER (PARTITION BY symbol ORDER BY ts, volume
                                  RANGE INTERVAL '1' HOUR PRECEDING)
FROM bars_small
