-- §3.4: the permitted wrapping is a CAST to a NUMERIC type. A cast to VARCHAR is not one.
SELECT COUNT(CAST(amount AS VARCHAR)) AS n FROM orders
