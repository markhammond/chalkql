-- A field in WHERE, and a composite's own NULL test: the filter reads the field and never the
-- composite value, and IS NOT NULL reads the composite's validity.
-- expect: has(Filter)
-- expect: has_field_access
SELECT q.id, q.symbol, q.bid.price AS bid_price
FROM quotes q
WHERE q.bid.price > 400 AND q.ask IS NOT NULL
ORDER BY q.id
