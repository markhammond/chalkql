-- §3.4 through a composite, class 8. A composite-valued aggregate is an aggregate like any other to
-- a population-only column's allow-list, and `amount_summary` is not on `amount`'s: for the three
-- auditors the statement is a POLICY refusal naming the function. No host can put it there either,
-- because registration refuses a user-defined aggregate in an allow-list (D190). For everyone else
-- the aggregate runs over what they may see, and a group with nothing to see is a NULL composite.
-- expect: principals(all)
-- expect: policy(u6)
-- expect: policy(u6-two-orgs)
-- expect: policy(u10-in-a-region)
SELECT org_id, amount_summary(amount).total AS total, amount_summary(amount).tally AS tally
FROM orders
GROUP BY org_id
ORDER BY org_id
