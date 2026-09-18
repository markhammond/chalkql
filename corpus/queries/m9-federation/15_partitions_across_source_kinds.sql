-- Design 39 §6. A partitioned table whose two partitions sit in two different *kinds* of source:
-- the history in DuckDB and the tail in an in-process POCO source. Corpus 06 and 07 span three
-- sources but all three speak SQL; here one partition has no dialect at all, so the scan cannot
-- become one remote query however the cost model would like it to — exactly one partition leaves as
-- SQL and the other is read in memory, and the union is the client's.
-- expect: has(PartitionedScan)
-- expect: count(RemoteQuery)=1
SELECT symbol, COUNT(*) AS bars, MAX(ts) AS latest
FROM federated.bars_across_kinds
GROUP BY symbol
ORDER BY symbol
