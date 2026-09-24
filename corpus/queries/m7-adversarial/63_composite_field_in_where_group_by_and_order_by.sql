-- §3.1 through a composite, class 8. A field in WHERE, GROUP BY and ORDER BY: the comparison, the
-- grouping and the sort all operate on what the function made of the disclosed value. An initial is
-- one character long, so an agent's rows fail the predicate; a placeholder is no value at all, so
-- a principal who sees nothing of the column gets no row; a fingerprint groups as itself.
-- expect: principals(all)
SELECT echo_with_length(first_name).echo AS echo, COUNT(*) AS n
FROM members
WHERE echo_with_length(first_name).len > 1
GROUP BY echo_with_length(first_name).echo
ORDER BY echo
