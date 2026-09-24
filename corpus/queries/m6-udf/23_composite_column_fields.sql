-- Fields of composite columns: `q.ask.price` is a field access over the column, as a field of a
-- function's composite is (D291), and a field of a NULL composite is NULL.
-- expect: count(FieldAccess)=4
SELECT q.id, q.bid.price AS bid_price, q.ask.price AS ask_price, q.ask.size AS ask_size,
       q.venue.country AS country
FROM quotes q
ORDER BY q.id
