-- §6 corpus 18. The same bound over a `UNION ALL` across two sources: it is copied into each
-- branch, and a branch whose source will sort for it carries it away as its own and renders the
-- number when the execution starts. The original stays above them as the global bound, so the
-- answer is decided here and a source that took the copy has only offered its first few
-- candidates. The SQLite branch is the other half of the claim: its profile honours no explicit
-- null placement, the sort gate declines the collation, and its copy stays a local heap — which
-- costs the same rows as before and is still a smaller heap than the whole branch.
-- expect: has(SetOp)
-- expect: count(RemoteQuery)=2
SELECT o_orderkey FROM duck.orders
UNION ALL
SELECT o_orderkey FROM sqlite.orders
ORDER BY o_orderkey
LIMIT ?
