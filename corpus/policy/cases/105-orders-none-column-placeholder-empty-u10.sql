-- §8 corpus 18: the same under PlaceholdersAsEmpty. `amount` is -1 under either policy; `note`
-- is the empty string and `placed_at` the epoch.
SELECT id, note, amount, placed_at FROM orders ORDER BY id
