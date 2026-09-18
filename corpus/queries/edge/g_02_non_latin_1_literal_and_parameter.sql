-- §0b: a string literal and a bound parameter outside Latin-1. Under Calcite's ISO-8859-1 default
-- this did not plan at all (F22, V46); the planner's character set is UTF-8 (V47), and the
-- comparison is by UTF-8 bytes, so every label sorts below the CJK literal.
-- expect: has(Filter)
SELECT id, label FROM sales
WHERE label < '株式会社' OR label = @tag
ORDER BY id
