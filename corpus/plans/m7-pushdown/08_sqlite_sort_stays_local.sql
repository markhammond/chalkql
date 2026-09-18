-- Full
source=sqlite dialect=sqlite parameters=0
SELECT "l_orderkey", "l_comment" FROM "lineitem"

-- FiltersOnly
source=sqlite dialect=sqlite parameters=0
SELECT "l_orderkey", "l_comment" FROM "lineitem"

-- ProjectionOnly
source=sqlite dialect=sqlite parameters=0
SELECT "l_orderkey", "l_comment" FROM "lineitem"

