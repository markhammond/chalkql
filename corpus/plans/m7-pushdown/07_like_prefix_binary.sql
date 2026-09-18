-- Full
source=duck dialect=duckdb parameters=0
SELECT "c_name" FROM (SELECT "c_name" FROM "customer") AS "t" WHERE "c_name" LIKE 'Customer#00001%' ORDER BY "c_name"

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "c_name" FROM (SELECT "c_name" FROM "customer") AS "t" WHERE "c_name" LIKE 'Customer#00001%'

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "c_name" FROM "customer"

