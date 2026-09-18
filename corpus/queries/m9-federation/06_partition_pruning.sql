-- §6 corpus 06. A partitioned table whose five partitions live in three sources. The predicate
-- names two symbols, so three partitions are pruned at planning and two remote calls are made.
-- expect: has(PartitionedScan)
-- expect: count(RemoteQuery)=2
SELECT symbol, COUNT(*) AS bars
FROM federated.bars_by_symbol
WHERE symbol IN ('BTCUSDT', 'XRPUSDT') AND ts < TIMESTAMP '2026-01-01 00:20:00'
GROUP BY symbol
ORDER BY symbol
