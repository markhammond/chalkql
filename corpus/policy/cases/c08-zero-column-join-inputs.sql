-- CALCITE-7487, CALCITE-4047: a projection over a join that reads no column from either input.
-- After trimming, PushProjector reaches its "nothing is projected from the children" branch,
-- which is where the ArrayIndexOutOfBoundsException lives.
SELECT 1 AS one
FROM members m JOIN orders o ON o.member_id = m.id
