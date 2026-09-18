-- D265 clause (h), design 38 §8. The second spelling of 40's semi-join, uncorrelated:
-- `o.id IN (SELECT i.order_id FROM order_items i JOIN items …)`. It must agree with 40 and 42 row
-- for row, as ADR 0050 §5 (a) says the three reach one shape.
-- expect: principals(all)
SELECT o.id FROM orders o
WHERE o.id IN (SELECT i.order_id FROM order_items i JOIN items p ON p.id = i.item_id)
ORDER BY o.id
