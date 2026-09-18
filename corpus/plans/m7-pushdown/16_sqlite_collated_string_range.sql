-- Full
source=sqlite dialect=sqlite parameters=0
SELECT "id", "region", "label" FROM (SELECT "id", "region", "label" FROM "sales") AS "t" WHERE "label" >= 'C' AND "label" < 'e' AND "region" > 'A'

-- FiltersOnly
source=sqlite dialect=sqlite parameters=0
SELECT "id", "region", "label" FROM (SELECT "id", "region", "label" FROM "sales") AS "t" WHERE "label" >= 'C' AND "label" < 'e' AND "region" > 'A'

-- ProjectionOnly
source=sqlite dialect=sqlite parameters=0
SELECT "id", "region", "label" FROM "sales"

