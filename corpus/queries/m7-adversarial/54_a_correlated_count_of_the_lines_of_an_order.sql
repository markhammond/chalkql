-- D265 clause (h), design 38 §8, class 2. A correlated count of the lines of each visible order.
-- The statement's own `order_items` is entitled by kind, so what a vendor counts is its own lines
-- and never the order's — which is what the kind-scoped `Inherited` buys over §3.13's kind-less
-- `Through`, and what ADR 0048 §7's first finding was about.
-- expect: principals(all)
SELECT o.id, (SELECT COUNT(*) FROM order_items i WHERE i.order_id = o.id) AS lines
FROM orders o
ORDER BY o.id
