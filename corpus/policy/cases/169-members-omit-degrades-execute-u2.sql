-- §3.11 (part 2b): Omit needs a constant NONE for the whole query, which execute-time binding
-- cannot give, so it degrades to Placeholder and the prepared query says so.
SELECT * FROM members ORDER BY id
