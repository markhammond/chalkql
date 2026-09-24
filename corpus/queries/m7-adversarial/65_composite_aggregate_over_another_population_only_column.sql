-- §3.4 and D261, class 8. The same aggregate over `national_id`, which the counter holds
-- population-only with COUNT its one permitted aggregate. Under a numeric CAST the column is still
-- a bare reference, and the function is still not on the column's allow-list: a POLICY refusal for
-- the counter. For everyone else the column is the placeholder, and the answer is a NULL composite.
-- expect: principals(all)
-- expect: policy(u9)
SELECT org_id, amount_summary(CAST(national_id AS BIGINT)).tally AS tally
FROM members
GROUP BY org_id
ORDER BY org_id
