-- F27 (ADR 0027). An integer division in a projection and inside a predicate, on the one dialect
-- Chalk pushes to whose `/` is real division. DuckDB 1.5.1 answers `5 / 2` as 2.5 and `-7 / 3` as
-- -2.3333333333333335, both DOUBLE, where SQLite, PostgreSQL and Chalk itself answer 2 and -2, so
-- Chalk's DuckDB dialect writes `//` for a DIVIDE whose result type is an integer kind.
--
-- The two halves fail differently and the query has both. In the projection the DOUBLE has to
-- satisfy a column Chalk declared an integer, so the scan contract catches it -- loudly, and with
-- the right answer nowhere in sight. Inside the predicate nothing catches it: `o_custkey / 3 = 2`
-- matches custkeys 6, 7 and 8 under integer division and only 6 under real division, and the rows
-- that came back would have looked perfectly plausible. That is what the differential against the
-- reference executor is for, and it is why this query exists at all: nothing in the corpus divided
-- integers on DuckDB before it.
--
-- The expression list is the substitution's own grouping hazard, the same one query 15 poses for
-- MOD (V54, ADR 0024): a substitute whose precedence differs from the call's silently loses or
-- gains parentheses. `//` binds like `*` in DuckDB -- measured: `1 + 6 // 3` is 3 and
-- `12 // 3 * 2` is 8 -- and the dialect brackets the substituted call anyway, so the golden shows
-- the grouping rather than relying on it.
--
-- `(0 - o_custkey) / 3` is the negative: TPC-H has no negative keys, and truncation towards zero
-- is only distinguishable from flooring on one. The last column is the half that is *not*
-- substituted -- a division that answers in a real is real division in Chalk and in all three
-- dialects -- so a reviewer sees both operators in the one generated statement. o_orderkey is
-- projected because the ORDER BY needs it: without it the plan keeps a Project over the remote
-- query just to drop the column, and `not(Project)` -- the claim that the whole expression list
-- was pushed and nothing computes locally -- could not be made.
-- expect: has(RemoteQuery)
-- expect: not(Project)
-- expect: not(Filter)
SELECT o_orderkey,
       o_custkey,
       o_custkey / 3 AS third,
       (0 - o_custkey) / 3 AS negative_third,
       o_custkey / 3 + 1 AS third_then_plus,
       10 - o_custkey / 3 AS third_on_the_right,
       (o_custkey + 1) / 3 AS sum_then_third,
       CAST(o_custkey AS DOUBLE) / 3 AS real_third
FROM duck.orders
WHERE o_custkey / 3 = 2
ORDER BY o_orderkey
