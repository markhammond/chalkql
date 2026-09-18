-- A3: EXCLUDE CURRENT ROW over a RANGE frame, whose bounds are peer-based and therefore determined
-- even though `amount` ties. Chalk implements the standard; `calcite-dotnet` pins Calcite's defect
-- here on purpose (ADR 0024).
-- expect: has(Window)
SELECT id, COUNT(amount) OVER (
         ORDER BY amount RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE CURRENT ROW) AS n
FROM sales ORDER BY id
