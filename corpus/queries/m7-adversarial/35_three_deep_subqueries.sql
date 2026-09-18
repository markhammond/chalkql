-- §3.1. Three levels of derived table between the scan and the root, in case the sanitiser is
-- applied at the root rather than at the leaf. It is applied at the leaf, so the depth changes
-- nothing at all.
-- expect: principals(all)
SELECT id, first_name FROM (
  SELECT id, first_name FROM (
    SELECT id, first_name FROM (
      SELECT id, first_name FROM members) a) b) c
ORDER BY id
