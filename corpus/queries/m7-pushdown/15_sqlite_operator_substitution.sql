-- §5 D (D168), the SQLite spelling of 14, and the dialect where the substitution actually happens:
-- SQLite's own mod() is a floating-point math function that answers in REAL and loses a wide
-- dividend through a double, so Chalk's SQLite dialect writes `a % b` instead (V53, ADR 0024). The
-- substitute binds like `*` where the call bound like an atom, so grouping is what can silently be
-- lost or wrongly added -- around the unary minus, around the sum inside the call, and between the
-- two mods that multiply. The golden SQL is the review signal; the differential at every level
-- proves the pushed answer is the local one.
--
-- l_linenumber is projected because the ORDER BY needs it: without it the plan keeps a Project over
-- the remote query just to drop the column, and `not(Project)` -- the claim that the whole
-- expression list was pushed and nothing computes locally -- could not be made.
--
-- Grafted from ikvmnet/calcite-dotnet (Apache-2.0), SqlServerModuloTests.cs and
-- SqlServerConcatenationTests.cs; see the repository's NOTICE. The dialect is not Chalk's and no
-- expected SQL is taken -- only the shapes worth generating (D163).
-- expect: has(RemoteQuery)
-- expect: not(Project)
SELECT l_orderkey,
       MOD(l_orderkey, 7) + 1 AS after_mod,
       10 - MOD(l_orderkey, 7) AS mod_on_the_right,
       -MOD(l_orderkey, 7) AS mod_under_a_prefix,
       MOD(l_orderkey + 1, 7) AS mod_over_a_sum,
       MOD(l_orderkey, 7) * MOD(l_linenumber, 3) AS two_mods,
       l_shipmode || '-' || l_returnflag AS joined,
       (l_shipmode || '-') || (l_returnflag || '!') AS grouped,
       l_linenumber
FROM sqlite.lineitem
WHERE MOD(l_orderkey, 7) = 3 AND l_orderkey < 200
ORDER BY l_orderkey, l_linenumber
