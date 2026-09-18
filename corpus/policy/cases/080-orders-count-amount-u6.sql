-- §3.6: COUNT over the column is itself guarded, and the guard reuses the call rather than
-- adding a second one.
SELECT COUNT(amount) AS n FROM orders
