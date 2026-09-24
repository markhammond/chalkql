-- A field the record does not have is Calcite's own validation error, with a position (D291).
-- expect: error=VALIDATION
-- expect: position
SELECT price_move("open", "close").nope FROM bars_small
