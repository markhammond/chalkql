-- D265 §8, §4. The other kind, for a principal holding no vendor grant at all: the vendor path's
-- endpoint predicate folds to FALSE, its chain is dropped, and the lines this principal sees are
-- the ones whose order its organisation reaches.
SELECT id, order_id, quantity, unit_price FROM order_items ORDER BY id
