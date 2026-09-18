-- D261 class 6. A sort key orders by the value, which is a comparison oracle by binary search and
-- not a shape any rule can name. The placeholder is what is sorted by, so the order is the tie-break.
-- expect: principals(all)
-- expect: policy(u9)
SELECT id FROM members ORDER BY national_id, id
