-- CALCITE-5345: UNION_PULL_UP_CONSTANTS removes a column every branch pins to the same literal
-- and re-adds it above the union. That is a width change under a set operation, so the pulled-up
-- constant has to land back in `org_id`'s position and nowhere else.
SELECT id, org_id, first_name FROM members WHERE org_id = 1
UNION ALL
SELECT id, org_id, first_name FROM members WHERE org_id = 1
