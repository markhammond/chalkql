-- §7 and ADR 0077, class 8. A client body that answers a composite is still a client body: it is
-- handed the column's disclosed form, so a field that echoes its input says what the host received
-- — the value, the initial, the fingerprint or nothing — and the length beside it is the length of
-- that, never of the name underneath.
-- expect: principals(all)
SELECT id, echo_with_length(first_name).echo AS echo, echo_with_length(first_name).len AS len FROM members ORDER BY id
