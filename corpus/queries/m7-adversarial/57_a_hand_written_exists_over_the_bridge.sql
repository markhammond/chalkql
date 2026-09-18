-- D265 clause (h), class 5. The mechanism's own shape written by hand over the bridge alone, with
-- no join to the endpoint: "some line exists for this order". What the sub-query reads is the
-- principal's own lines, so the answer is the orders it can see that have a line it can see — never
-- the orders that have a line somebody else can see.
-- expect: principals(all)
SELECT o.id FROM orders o WHERE EXISTS (SELECT 1 FROM order_items i WHERE i.order_id = o.id)
ORDER BY o.id
