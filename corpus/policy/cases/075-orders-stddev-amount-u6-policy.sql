-- §3.4: STDDEV_POP is in D190's permitted set but not in this column's allow-list, so it is
-- refused naming the function.
SELECT STDDEV_POP(amount) AS s FROM orders
