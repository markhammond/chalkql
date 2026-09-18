-- §3.7, D199: the tenancy predicate reaches the source. `row_predicate_pushed` is true and the
-- remote query text carries the IN list.
SELECT * FROM members ORDER BY id
