-- D265 clause (h), design 38 §0 and §8. The mechanism written out by hand: the first of the three
-- spellings of one semi-join, and the one the statement correlates. Each must answer what the
-- mechanism answers — the rows are the principal's own either way, because every table in it is
-- entitled as its own policy says.
-- expect: principals(all)
SELECT o.id FROM orders o
WHERE EXISTS (SELECT 1 FROM order_items i JOIN items p ON p.id = i.item_id WHERE i.order_id = o.id)
ORDER BY o.id
