-- D266 §6, class 3. A join through `members` back to `orders`, to reach an order outside the
-- confinement by way of the member who placed one inside it. The second occurrence of `orders` is
-- entitled exactly as the first is, and `members` resolves no region at all, so a confined grant
-- reaches no member row and the correlation has nothing to stand on.
-- expect: principals(all)
SELECT o2.id AS oid FROM orders o1
JOIN members m ON m.id = o1.member_id
JOIN orders o2 ON o2.member_id = m.id
ORDER BY o2.id
