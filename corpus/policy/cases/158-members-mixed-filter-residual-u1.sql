-- §8 corpus 16 and §3.7 item 3: a mixed filter. The tenancy conjunct and the postcode equality
-- are pushed; the client-bodied function stays above the boundary as the residual.
SELECT id FROM members WHERE postcode = '2000' AND is_vip(id) ORDER BY id
