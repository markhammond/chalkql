-- D261 class 6. A function of the column: the operand the shape needs is a *bare* reference, so
-- UPPER(national_id) = ? is not one, and what UPPER receives is the placeholder.
-- expect: principals(all)
-- expect: policy(u9)
SELECT id FROM members WHERE UPPER(national_id) = ? ORDER BY id
