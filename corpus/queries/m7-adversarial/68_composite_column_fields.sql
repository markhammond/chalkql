-- The same card taken apart: a field of a withheld card is NULL, never the stored value, because the
-- field is read from the disclosed composite and not from the column underneath it.
-- expect: principals(all)
SELECT p.id, p.contact.email AS email, p.contact.phone AS phone, p.contact.tier AS tier
FROM main.profiles p
ORDER BY p.id
