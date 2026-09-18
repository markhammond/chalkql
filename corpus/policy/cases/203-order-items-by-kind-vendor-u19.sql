-- D265 §8. The bridge, entitled by kind: a vendor sees the lines that carry its own goods and no
-- other line of an order it can see, and of those lines it reads the quantity and not the price.
SELECT id, order_id, quantity, unit_price FROM order_items ORDER BY id
