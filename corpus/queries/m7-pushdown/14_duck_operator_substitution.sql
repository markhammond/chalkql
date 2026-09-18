-- §5 D (D168): a pushed expression that mixes MOD and concatenation with operators of adjacent
-- precedence. Where a dialect rewrites an operator -- MOD(a, b) into `a % b`, `||` into something
-- else -- the substitute's precedence differs from the call's, and grouping is silently lost or
-- wrongly added. The golden SQL is the review signal; the differential proves the pushed answer is
-- the local one.
--
-- This is the half where nothing is substituted: DuckDB's MOD is an exact integer function, so the
-- dialect writes the call through and the golden is what an unsubstituted expression list looks
-- like. Query 15 is the same list on the dialect that does substitute (V53), and reading the two
-- goldens side by side is the point of having both.
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
FROM duck.lineitem
WHERE MOD(l_orderkey, 7) = 3 AND l_orderkey < 200
ORDER BY l_orderkey, l_linenumber
