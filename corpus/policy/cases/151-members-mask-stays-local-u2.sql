-- §3.8, D200: an agent's LIKE is over a withheld column, so it is not pushed; it is evaluated
-- locally over the rows the pushed Filter_R returned, and the plan text shows the residual.
SELECT id FROM members WHERE first_name LIKE 'T%' ORDER BY id
