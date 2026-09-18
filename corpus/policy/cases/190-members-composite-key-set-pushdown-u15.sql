-- F50 (ADR 0027): a bound list of *pairs* above the fold ceiling is pushed as a key set over both
-- columns, `(id, org_id) IN ((?, ?), …)`, in calls sized by the source's own ceiling. Before it the
-- rule matched a single key only, so the entitled table came back whole and `row_predicate_pushed`
-- was honestly false. Compare case 159, the same shape over a one-column list.
SELECT id FROM members ORDER BY id
