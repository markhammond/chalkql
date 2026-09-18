-- A1: ordered by the partition key first, so both partitions' NULLs are reached.
-- expect: has(Sort)
SELECT id, region, amount FROM sales ORDER BY region, amount NULLS FIRST, id
