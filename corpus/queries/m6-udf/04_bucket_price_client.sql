-- §5 corpus 04. A client body travels by name and is evaluated locally; nothing folds it, and the
-- delegate allocates nothing, which is what the allocation gate asserts.
-- expect: has_user_function(main.bucket_price)
SELECT symbol, ts, bucket_price("close", 5.0) AS bucket
FROM bars_small
ORDER BY ts, symbol
