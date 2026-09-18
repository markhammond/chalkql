-- D265 clause (h), design 38 §8. The target joined to the bridge on the key. The statement's own
-- occurrence of `order_items` is entitled as the line's own policy says — by kind, so a vendor
-- reaches the lines that carry its own goods and no other line of an order it can see — while the
-- mechanism's occurrence of the same table, inside the order's key set, is not the statement's and
-- is never reachable from it (§7). `quantity` is full for whoever sees the line and `unit_price` is
-- the placeholder for a vendor, so one statement shows both halves of §8's column rules.
-- expect: principals(all)
SELECT o.id, i.id, i.quantity, i.unit_price
FROM orders o JOIN order_items i ON i.order_id = o.id
ORDER BY o.id, i.id
