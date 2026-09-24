-- `t.c.*` over the entitled composite column: the expansion reads the disclosed composite, so every
-- field it yields is withheld wherever the card is.
-- expect: principals(all)
SELECT p.id, p.contact.* FROM main.profiles p ORDER BY p.id
