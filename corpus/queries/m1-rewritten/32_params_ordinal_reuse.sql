SELECT symbol, ts FROM bars
WHERE ts >= ? AND symbol = ? AND (trade_count IS NULL OR ts < ? AND ts >= ?)
