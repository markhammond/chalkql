-- expect: has_function(LIKE)
SELECT symbol, ts FROM bars WHERE symbol LIKE 'BTC%'
