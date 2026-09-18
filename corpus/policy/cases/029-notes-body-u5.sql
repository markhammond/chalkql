-- §8 corpus 10: no grants, no rows, no error. See the case -- the reported visibility is SOME
-- and not NONE, because the created-by disjunct cannot fold away (README §5, finding F1).
SELECT body FROM notes
