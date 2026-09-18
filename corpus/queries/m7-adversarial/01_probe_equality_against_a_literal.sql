-- §3.1. The simplest oracle there is: guess a value and ask whether a row has it. The predicate
-- compares the *disclosed* value, so a manager's rows answer about the name and an agent's about
-- the initial — the most the guess can learn is what the mask already showed.
-- expect: principals(all)
SELECT id FROM members WHERE first_name = 'T' ORDER BY id
