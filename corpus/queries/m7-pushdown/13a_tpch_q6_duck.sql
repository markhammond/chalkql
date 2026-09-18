-- §5 corpus 13. TPC-H Q6 against `duck`, at every level, compared with the POCO copy.
-- expect: has(RemoteQuery)
SELECT SUM(l_extendedprice * l_discount) AS revenue
FROM duck.lineitem
WHERE l_shipdate >= DATE '1995-01-01'
  AND l_shipdate < DATE '1996-01-01'
  AND l_discount BETWEEN 0.05 AND 0.07
  AND l_quantity < 24
