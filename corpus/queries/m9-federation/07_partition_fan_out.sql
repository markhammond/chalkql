-- §6 corpus 07. Nothing to prune, so every partition is read — five of them, across three sources,
-- concurrently. The union claims no ordering, which is why this query asks for one.
-- expect: has(PartitionedScan)
-- expect: count(RemoteQuery)=5
SELECT symbol, ts, "close"
FROM federated.bars_by_symbol
WHERE ts < TIMESTAMP '2026-01-01 00:10:00'
ORDER BY symbol, ts
