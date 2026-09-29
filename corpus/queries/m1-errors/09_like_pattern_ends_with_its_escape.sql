-- expect: error=VALIDATION
-- expect: position
SELECT symbol FROM bars WHERE symbol LIKE 'BTC!' ESCAPE '!'
