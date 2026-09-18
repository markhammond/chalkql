-- CALCITE-7622, CALCITE-6504, CALCITE-5213, CALCITE-2136: EXISTS becomes a LEFT MARK correlate,
-- decorrelates into a LEFT MARK join and then a semi join, under a projection whose column order
-- is not the table's. A semi join's output is not left+right, which is the assumption the
-- transpose rules' scratch row type makes.
SELECT m.postcode, m.first_name, m.id
FROM members m
WHERE EXISTS (SELECT 1 FROM orders o WHERE o.member_id = m.id)
