-- Tier 1 widened (D298): a client body over two DECIMAL(15, 2) arguments, written in CLR decimal.
-- The amounts reach the delegate exactly, and its DECIMAL(15, 2) answer is written back the same
-- way — which before D298 was refused at registration, pointing at a Tier 2 kernel.
-- expect: has_user_function(main.net_amount)
SELECT l_orderkey, l_linenumber, net_amount(l_extendedprice, l_discount) AS net
FROM lineitem
WHERE l_orderkey < 100
ORDER BY l_orderkey, l_linenumber
