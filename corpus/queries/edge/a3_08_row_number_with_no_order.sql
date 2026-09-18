-- A3: ROW_NUMBER() over a partition with no ORDER BY. The numbers a row gets are not determined by
-- SQL, only their multiset per partition is; both Chalk executors read the fixture in one order and
-- agree, and the DuckDB oracle is not asked (ADR 0024).
-- expect: has(Window)
SELECT id, ROW_NUMBER() OVER (PARTITION BY region) AS rn FROM sales ORDER BY id
