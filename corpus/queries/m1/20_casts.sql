-- expect: has(Project)
-- expect: count(Cast)=3
-- The design says "two Cast nodes", meaning the two the SQL asks for. Operand homogeneity
-- (I-IR-2) adds a third: `/(DOUBLE, 1000)` needs the integer literal cast to FP64 so the
-- executor's kernel can be closed over one type. See docs/design/03-planner.md §5.3.
SELECT CAST(volume AS DOUBLE) / 1000 AS kvol, CAST(ts AS DATE) AS d FROM bars
