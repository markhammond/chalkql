-- §6 corpus 01. The client-zero shape: a five-row local dimension against a large remote fact
-- table. The keys travel, the rows do not — one call carrying five symbols, and `RowsFetched` is
-- the matching rows rather than all 100 800 of them.
--
-- The bars are narrowed on `volume` rather than on `ts` because DuckDB's profile declares six
-- digits of timestamp precision and Chalk's TIMESTAMP is nine, so D89 refuses to push a temporal
-- comparison it would truncate — and a lookup side whose predicate cannot be pushed is not a lookup
-- side at all (ADR 0022).
-- expect: has(LookupJoin)
-- expect: has(RemoteQuery)
-- expect: not(HashJoin)
SELECT s.symbol, s."base", b.ts, b."close"
FROM symbols s JOIN duck.bars b ON b.symbol = s.symbol
WHERE b.volume > 9900
ORDER BY b.ts, s.symbol
