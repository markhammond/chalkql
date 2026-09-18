-- §3.6: COUNT(*) is not over the column and is not guarded; it is reported Full.
SELECT COUNT(*) AS n FROM orders
