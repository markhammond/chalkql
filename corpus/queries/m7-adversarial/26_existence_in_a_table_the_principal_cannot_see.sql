-- §3.7, §3.12. No value at all, only a count — the acknowledgement principle's own case: a count of
-- rows the principal may not see would be a fact about hidden rows. The count is taken after the
-- row predicate, so it counts what they may see and nothing else.
-- expect: principals(all)
SELECT COUNT(*) AS n FROM notes WHERE org_id = 2
