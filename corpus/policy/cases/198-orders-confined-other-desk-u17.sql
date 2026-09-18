-- D266. A subject grant confined along two tenancy kinds at once, in layer A's own vocabulary: one
-- membership over a tuple of arity three, and all three of its columns must match the same row.
SELECT id, member_id, org_id, created_by FROM orders ORDER BY id
