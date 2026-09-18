-- §36.4. The predicate form: the same substitution, in a WHERE. For the support desk the leaf's
-- derived boolean is the filter; for everybody else the placeholder is compared and no row matches.
-- expect: principals(all)
-- expect: tested
-- expect: policy(u9)
SELECT id FROM members WHERE national_id = ? ORDER BY id
