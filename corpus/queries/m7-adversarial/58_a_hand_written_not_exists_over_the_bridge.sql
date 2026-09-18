-- D265 clause (h), class 6. The negation, which is the shape that leaks when a sub-query reads more
-- rows than the principal holds: "the orders with no line at all" is a different set for a vendor
-- than for the host, and it must be the vendor's own.
-- expect: principals(all)
SELECT o.id FROM orders o WHERE NOT EXISTS (SELECT 1 FROM order_items i WHERE i.order_id = o.id)
ORDER BY o.id
