-- Class 8. The same composite carried whole: both fields travel in one Arrow struct column and are
-- what the function made of the disclosed value. Where the principal gets the placeholder the
-- function, being strict, is never called, and the composite is NULL.
-- expect: principals(all)
SELECT id, echo_with_length(first_name) AS d FROM members ORDER BY id
