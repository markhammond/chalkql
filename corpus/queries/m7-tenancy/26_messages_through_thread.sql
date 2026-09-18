-- §3.13 corpus 26. The star over a table entitled *through* its parent, as every principal. A
-- message carries no tenancy column at all: which rows come back is decided by the thread, and what
-- `content` discloses is decided by the roles held in the thread's organisation — u1 manages O1 and
-- acts as an agent in O2, so the O1 threads' messages come back whole and O2's as an excerpt; u3
-- holds a subject grant on the member thread 3 is about and sees that thread's message in full; u4
-- holds the global grant, for whom the parent's predicate folds to TRUE and the join is elided
-- altogether; u5 holds nothing and the derived visibility is NONE.
-- expect: principals(all)
SELECT * FROM messages ORDER BY id
