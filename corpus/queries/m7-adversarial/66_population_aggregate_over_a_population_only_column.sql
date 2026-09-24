-- §3.4 through a composite, class 8, and the permitted twin of 64. `population_summary` is
-- `amount_summary` under a second name, declared population-safe by its host (D295): its total and
-- its tally report the group and never one row, so `amount`'s allow-list may name it and does. For
-- the three auditors it runs, and the group-size guard NULLs the composite of every group smaller
-- than the column's floor, whole. For everyone else it runs over what they may see, exactly as 64.
-- expect: principals(all)
-- expect: guarded
SELECT org_id, population_summary(amount).total AS total, population_summary(amount).tally AS tally
FROM orders
GROUP BY org_id
ORDER BY org_id
