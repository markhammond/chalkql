-- §3.2: a constant mask makes every visible row equal to the mask literal, order 111 included,
-- whose raw note is NULL.
SELECT id FROM orders WHERE note = '********' ORDER BY id
