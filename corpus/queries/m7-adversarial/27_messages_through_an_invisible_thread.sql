-- §3.13. A message carries no tenancy column, so a statement that names its thread by key is asking
-- the child about a parent the principal cannot see. Visibility derives through the parent's own
-- entitled scan, so the key is just a key and the rows stay invisible.
-- expect: principals(all)
SELECT id, content FROM messages WHERE thread_id = 4 ORDER BY id
