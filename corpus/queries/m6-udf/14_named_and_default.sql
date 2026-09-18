-- §5 corpus 14. A named-argument call and one that omits an optional parameter: the arguments are
-- permuted into declaration order and the default is substituted, both before validation.
-- expect: has_user_function(main.bucket_price)
SELECT symbol, ts,
       bucket_price(width => 5.0, price => "close", off => 1.0) AS named,
       bucket_price("close", 5.0) AS defaulted
FROM bars_small
ORDER BY ts, symbol
