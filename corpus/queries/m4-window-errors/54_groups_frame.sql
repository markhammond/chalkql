-- Calcite 1.42's parser does not accept GROUPS at all (ADR 0017, V16), so the IR's FRAME_MODE_GROUPS
-- is reserved and unreachable and the failure is a parse error rather than UNSUPPORTED.
-- expect: error=PARSE
-- expect: position
SELECT symbol, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts
                                 GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW)
FROM bars_small
