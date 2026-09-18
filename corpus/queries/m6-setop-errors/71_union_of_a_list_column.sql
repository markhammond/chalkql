-- D58 and D69: a UNION compares whole rows, and a v1 LIST has no equality, so a DISTINCT union of
-- one is refused rather than answered wrongly.
-- expect: error=UNSUPPORTED
SELECT tags FROM symbols
UNION
SELECT tags FROM symbols
