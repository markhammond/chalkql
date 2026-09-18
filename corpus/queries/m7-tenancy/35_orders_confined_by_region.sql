-- D266 corpus 35. The star over `orders` minus `amount`, as every principal: which rows a grant
-- reaches is the whole of the point here, and `amount` is population-only for the auditor roles, so
-- naming it would make this a POLICY refusal for them exactly as query 03 already is. u10 audits O1
-- *confined to region 2*, so it sees the two O1 orders in R2 and neither the O1 orders in R1 nor any
-- other organisation's order in R2 — the conjunction is inside the one grant, and a union of two
-- grants would have given all of them. u11 holds a subject grant on member 1 confined to O1 and R1
-- at once, so of that member's two orders it sees the one in R1.
-- expect: principals(all)
SELECT id, member_id, org_id, region_id, note FROM orders ORDER BY id
