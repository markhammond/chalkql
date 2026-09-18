-- §3.1. Containment, which is the cheapest character oracle of the three: it asks whether the value
-- holds a letter anywhere. Over the mask it asks about the initial and nothing else.
-- expect: principals(all)
SELECT id FROM members WHERE first_name LIKE '%a%' ORDER BY id
