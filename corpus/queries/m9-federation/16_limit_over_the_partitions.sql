-- §6 corpus 16. A bound above the partitions is copied into every one of them, so each source
-- offers its first few candidates rather than all of its rows and the global sort picks the answer
-- from those. The original bound stays where it was: it is still what decides the result.
-- expect: has(PartitionedScan)
-- expect: count(RemoteQuery)=5
-- expect: count(TopN)=6
SELECT symbol, ts, "close"
FROM federated.bars_by_symbol
ORDER BY symbol, ts
LIMIT 4
