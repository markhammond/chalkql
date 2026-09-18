-- §3.8. The same dictionary through `LIKE` rather than equality, which is the version that works
-- against a value the attacker can only guess the beginning of. The patterns are constants because
-- M1 compiles a LIKE pattern once per plan (`02-ir.md` §6), which is why this is a disjunction and
-- not a join to a list. Against an initial mask a prefix of four never matches; against a manager's
-- rows it does, and that is the disclosure the manager already has.
-- expect: principals(all)
SELECT id, first_name FROM members
WHERE first_name LIKE 'Tara%' OR first_name LIKE 'Bo%' OR first_name LIKE 'Ada%'
ORDER BY id
