-- §3.6: a principal for whom the column is FULL gets no guard and no `Aggregate` in the report,
-- over the same statement and the same table.
SELECT org_id, SUM(amount) AS s FROM orders GROUP BY org_id ORDER BY org_id
