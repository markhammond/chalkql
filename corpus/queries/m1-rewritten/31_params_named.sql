SELECT symbol, ts, "close" FROM bars
WHERE symbol = ? AND ts >= ? AND ts < ?
