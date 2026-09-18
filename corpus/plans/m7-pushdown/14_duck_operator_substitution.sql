-- Full
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", MOD("l_orderkey", 7) + 1 AS "after_mod", 10 - MOD("l_orderkey", 7) AS "mod_on_the_right", - MOD("l_orderkey", 7) AS "mod_under_a_prefix", MOD("l_orderkey" + 1, 7) AS "mod_over_a_sum", MOD("l_orderkey", 7) * MOD("l_linenumber", 3) AS "two_mods", "l_shipmode" || '-' || "l_returnflag" AS "joined", "l_shipmode" || '-' || ("l_returnflag" || '!') AS "grouped", "l_linenumber" FROM (SELECT "l_orderkey", "l_linenumber", "l_returnflag", "l_shipmode" FROM "lineitem") AS "t" WHERE MOD("l_orderkey", 7) = 3 AND "l_orderkey" < 200 ORDER BY "l_orderkey", "l_linenumber"

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", MOD("l_orderkey", 7) + 1 AS "after_mod", 10 - MOD("l_orderkey", 7) AS "mod_on_the_right", - MOD("l_orderkey", 7) AS "mod_under_a_prefix", MOD("l_orderkey" + 1, 7) AS "mod_over_a_sum", MOD("l_orderkey", 7) * MOD("l_linenumber", 3) AS "two_mods", "l_shipmode" || '-' || "l_returnflag" AS "joined", "l_shipmode" || '-' || ("l_returnflag" || '!') AS "grouped", "l_linenumber" FROM (SELECT "l_orderkey", "l_linenumber", "l_returnflag", "l_shipmode" FROM "lineitem") AS "t" WHERE MOD("l_orderkey", 7) = 3 AND "l_orderkey" < 200

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_linenumber", "l_returnflag", "l_shipmode" FROM "lineitem"

