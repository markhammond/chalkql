-- A window table function's operands are validated in a scope that has no table, so a column
-- reference there is "not found" rather than "not constant" (V23, ADR 0018).
-- expect: error=VALIDATION
-- expect: position
SELECT * FROM TABLE(HOP(TABLE bars_small, DESCRIPTOR(ts), ts, INTERVAL '5' MINUTE))
