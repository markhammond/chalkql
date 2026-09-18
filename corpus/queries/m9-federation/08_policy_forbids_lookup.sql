-- §6 corpus 08. Corpus 01 again, under a per-request policy that forbids `LOOKUP` for this pair of
-- sources. The strategy falls back to `LOCAL` — the fallback that always exists — the answer is the
-- same, and the digest is not: a policy is part of what a plan is.
-- join-policy: pairs=[mem -> duck allowed=LOCAL]
-- expect: has(RemoteQuery)
-- expect: not(LookupJoin)
-- expect: not(AdaptiveJoin)
SELECT s.symbol, s."base", b.ts, b."close"
FROM symbols s JOIN duck.bars b ON b.symbol = s.symbol
WHERE b.volume > 9900
ORDER BY b.ts, s.symbol
