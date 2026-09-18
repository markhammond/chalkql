-- §6 corpus 09. The guardrail: a policy that forbids every strategy but `LOCAL` for this pair, and
-- caps what a local join may pull across a boundary at ten thousand rows. The plan would fetch
-- sixty thousand, so planning fails naming the join and the estimate rather than running for a
-- minute and then answering.
-- join-policy: local_join_max_rows=10000, pairs=[* -> * allowed=LOCAL]
-- expect: error=UNSUPPORTED
SELECT COUNT(*)
FROM supplier s JOIN duck.lineitem l ON l.l_suppkey = s.s_suppkey
