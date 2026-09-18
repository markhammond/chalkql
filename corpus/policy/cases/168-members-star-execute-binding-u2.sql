-- §2 (part 2b): execute-time binding. One shared plan across principals, the sanitiser evaluated
-- per row, and the same rows as under prepare-time binding.
SELECT * FROM members ORDER BY id
