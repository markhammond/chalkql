-- D265 clause (h), class 4. The lines of an order that carry somebody else's goods. Order 1 carries
-- one line of each vendor, so a principal holding V1's grant can see the order and must still see
-- none of V2's line on it — the bridge's own policy decides that, and the mechanism's occurrence of
-- the bridge is not the statement's.
-- expect: principals(all)
SELECT o.id, i.id, p.vendor_id
FROM orders o JOIN order_items i ON i.order_id = o.id JOIN items p ON p.id = i.item_id
WHERE p.vendor_id <> 1
ORDER BY o.id, i.id
