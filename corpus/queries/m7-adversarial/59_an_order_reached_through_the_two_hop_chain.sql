-- D265 clause (h), class 7. The chain walked the other way, from the endpoint outward: a vendor, its
-- items, the lines that carry them and the orders those lines belong to. Four entitled tables in one
-- statement, each answering for itself, and the order's own columns still what the order's rules
-- say — which is the two-hop shape `attachments` already exercises for `through`.
-- expect: principals(all)
SELECT v.name, o.id, o.org_id
FROM vendors v
JOIN items p ON p.vendor_id = v.id
JOIN order_items i ON i.item_id = p.id
JOIN orders o ON o.id = i.order_id
ORDER BY v.name, o.id
