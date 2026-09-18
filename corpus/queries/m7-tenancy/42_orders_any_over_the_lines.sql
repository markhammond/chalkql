-- D265 clause (h), design 38 §8. The third spelling, the quantified one — which also tells whether
-- the conformance level accepts `= ANY (subquery)` at all. It must agree with 40 and 41.
-- expect: principals(all)
SELECT o.id FROM orders o
WHERE o.id = ANY (SELECT i.order_id FROM order_items i JOIN items p ON p.id = i.item_id)
ORDER BY o.id
