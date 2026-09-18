-- §7. A client body runs in the host's own process, which is the one place a raw value would be
-- invisible to the plan. It receives the column's *disclosed* form, and it never pushes.
-- expect: principals(all)
SELECT id, echo(first_name) AS seen FROM members ORDER BY id
