-- §5 corpus 07. A Tier 2 kernel: the host is handed the whole batch and writes the whole batch, with
-- nothing allocated per row.
-- expect: has_user_function(main.fast_hash)
SELECT symbol, ts, fast_hash(symbol) AS h
FROM bars_small
ORDER BY ts, symbol
