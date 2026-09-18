-- `geo_mean` is not declared WINDOW, so it may only be used with GROUP BY. Calcite refuses the frame
-- because `allowsFraming` is false.
-- expect: error=VALIDATION
SELECT symbol, geo_mean("close") OVER (PARTITION BY symbol ORDER BY ts ROWS 3 PRECEDING)
FROM bars_small
