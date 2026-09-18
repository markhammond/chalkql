-- §3.6: a global group of two rows. COUNT(*) answers; the sum does not.
SELECT COUNT(*) AS n, SUM(amount) AS s FROM orders WHERE org_id = 3
