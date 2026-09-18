-- D58: a LIST has no ordering.
-- expect: error=UNSUPPORTED
SELECT symbol, tags FROM symbols ORDER BY tags
