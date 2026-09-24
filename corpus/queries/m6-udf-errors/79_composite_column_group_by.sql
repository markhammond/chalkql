-- GROUP BY a composite column is refused by name: rows cannot be grouped by a value with no equality.
-- expect: error=UNSUPPORTED
SELECT bid, COUNT(*) AS n FROM quotes GROUP BY bid
