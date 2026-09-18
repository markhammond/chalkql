-- §8 corpus 18, D162: a per-column placeholder of -1 on `amount` wins over PlaceholdersAsNull.
-- Under this variant the creator rule is not prepended, so u10's own rows are visible and their
-- protected columns are not disclosed (CreatorSees = ByRules).
SELECT id, note, amount, placed_at FROM orders ORDER BY id
