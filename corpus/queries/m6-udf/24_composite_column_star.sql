-- `t.c.*` expands a composite column into its fields, as `s.c.*` does over a subquery's composite.
-- expect: count(FieldAccess)=2
SELECT q.id, q.bid.*
FROM quotes q
ORDER BY q.id
