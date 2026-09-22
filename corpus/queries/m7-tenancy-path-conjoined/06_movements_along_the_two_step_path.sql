-- The same conjunction one step further out: a movement inherits its supplier through its position,
-- which inherits it through its product, so the path the compiler flattens has two steps and the
-- chain two joins.
-- expect: principals(all)
SELECT id, delta FROM movements ORDER BY id
