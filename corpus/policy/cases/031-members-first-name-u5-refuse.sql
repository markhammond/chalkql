-- D207 with a genuine visibility = NONE: `members` carries no created-by fail-safe, so u5's row
-- predicate folds to FALSE and RefuseWhenNoVisibleRows turns that into POLICY at prepare.
SELECT first_name FROM members
