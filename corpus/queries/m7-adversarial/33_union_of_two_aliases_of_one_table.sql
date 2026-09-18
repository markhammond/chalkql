-- §3.1, §3.12. One table under two predicates, unioned, so a mixed principal's two tenancies meet
-- in one column. Each occurrence is its own leaf with its own disclosure map, and the reported
-- disclosure of the union is `PerRow` rather than the better of the two — a masked column unioned
-- with a full one is alternative rows, not two origins of one value.
-- expect: principals(all)
SELECT id, first_name FROM members WHERE org_id = 1
UNION ALL
SELECT id, first_name FROM members WHERE org_id = 2
ORDER BY id
