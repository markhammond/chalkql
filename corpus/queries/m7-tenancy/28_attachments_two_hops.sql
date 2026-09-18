-- §3.13 corpus 28. Two hops: an attachment is visible when its message is, and a message when its
-- thread is. The pass recurses, so the child's leaf carries a join to a parent whose own leaf
-- carries a join to *its* parent, and the row predicate a principal never wrote reaches both.
-- expect: principals(all)
SELECT * FROM attachments ORDER BY id
