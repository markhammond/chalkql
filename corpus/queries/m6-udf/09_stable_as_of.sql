-- §5 corpus 09. A STABLE call of constants is evaluated once per execution and broadcast, so one
-- value stands across the whole result.
-- expect: has_user_function(main.as_of)
SELECT symbol, ts, as_of() AS as_of_value
FROM bars_small
ORDER BY ts, symbol
