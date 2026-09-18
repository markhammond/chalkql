-- §3.6: COUNT(DISTINCT c) is allow-listed here and guarded like every other call over c.
SELECT COUNT(DISTINCT amount) AS n FROM orders
