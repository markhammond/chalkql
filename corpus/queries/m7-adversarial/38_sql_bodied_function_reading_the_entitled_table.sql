-- §7. A SQL body is inlined before the rewrite, so the table reference inside it is entitled like
-- any other and the caller's own grants decide what comes back. A principal who may not see an
-- organisation gets nothing from a function whose body names it.
-- expect: principals(all)
SELECT id, first_name FROM TABLE(members_in(2)) ORDER BY id
