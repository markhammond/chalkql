-- A composite value has no equality either (D291). Calcite validates a comparison of two and would
-- fold `f(x) = f(x)` to TRUE before anything saw it; this one compares two different calls. Both are
-- refused by name when the statement is validated.
-- expect: error=UNSUPPORTED
SELECT symbol, ts FROM bars_small WHERE price_move("open", "close") = price_move("close", "open")
