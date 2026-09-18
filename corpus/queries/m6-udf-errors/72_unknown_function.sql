-- A name the catalog does not declare is not a Chalk function; the validator says so with a position.
-- expect: error=VALIDATION
-- expect: position
SELECT no_such_function(symbol) FROM bars_small
