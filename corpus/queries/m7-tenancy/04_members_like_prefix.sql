-- §8 corpus 04, and D195's whole point: the predicate compares the *disclosed* value, so a manager
-- gets their organisation's rows whose name starts with T and an agent's organisation's rows whose
-- initial is T. The earlier drafts' query-field rule would have returned nothing.
-- expect: principals(all)
-- D261: `national_id` is population-only for the counting role, so reading it as a value is
-- refused for that principal exactly as any population-only column is (§3.4).
-- expect: policy(u9)
SELECT * FROM members WHERE first_name LIKE 'T%' ORDER BY id
