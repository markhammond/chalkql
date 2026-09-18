-- A self-join on an unentitled key: two leaves, two Filter_R, and the tenancy holds on both.
SELECT a.id AS a_id, b.id AS b_id
FROM orders a JOIN orders b ON a.member_id = b.member_id AND a.id < b.id
ORDER BY a_id, b_id
