-- The design named PERCENTILE_CONT here; Calcite rejects that one at validation ("OVER must be
-- applied to aggregate function"), so the UNSUPPORTED case is an aggregate the IR has no id for
-- (ADR 0017).
-- expect: error=UNSUPPORTED
SELECT symbol, COLLECT(volume) OVER (PARTITION BY symbol ORDER BY ts) FROM bars_small
