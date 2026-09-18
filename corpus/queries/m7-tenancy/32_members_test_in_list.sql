-- §36.4. `IN (list)`: one lifted comparison over the whole list, because the list is what the rule
-- permits. A comparison with a *column* is refused precisely so that a VALUES list cannot turn one
-- probe into a thousand (§36.3), and a list of parameters is still one probe.
-- expect: principals(all)
-- expect: tested
-- expect: policy(u9)
SELECT id FROM members WHERE national_id IN (?, ?) ORDER BY id
