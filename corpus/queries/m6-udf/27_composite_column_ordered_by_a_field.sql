-- Ordered by a field: the field is projected below the top-N sort, so nothing sorts a composite
-- value, and the composite column itself is carried through it whole.
-- expect: has(TopN)
-- expect: has_field_access
SELECT q.id, q.ask
FROM quotes q
WHERE q.ask IS NOT NULL
ORDER BY q.ask.price DESC, q.id
LIMIT 10
