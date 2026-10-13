WITH days AS (SELECT * FROM day_windows('America/New_York', 17) WHERE days_ago < 1),
p AS (
  SELECT d.day_label, CAST('risk' AS VARCHAR(32)) AS leg, k.asset, k.client, k.channel, CAST('-' AS VARCHAR(32)) AS book, CAST('-' AS VARCHAR(32)) AS venue, k.counter_symbol,
         CASE WHEN c.amt_signed < 0 THEN 'long' WHEN c.amt_signed > 0 THEN 'short' ELSE 'flat' END AS direction,
         -c.amt_signed AS qty,
         -c.amt_signed * m.usd_price AS notional,
         -(c.cumulative_realized_pnl - COALESCE(o.cumulative_realized_pnl, 0)) * q.usd_rate AS realized,
         -c.amt_signed * (m.usd_price - c.avg_usd_prc) AS unrealized,
         -c.cumulative_realized_pnl * q.usd_rate AS balance,
         (c.cumulative_volume - COALESCE(o.cumulative_volume, 0)) AS volume,
         c.trade_count - COALESCE(o.trade_count, 0) AS trades
  FROM days d
  CROSS JOIN (SELECT p.account_id, p.channel, x.party_name AS client, y.underlying AS asset, y.quote AS counter_symbol
              FROM (SELECT account_id, channel FROM positions GROUP BY account_id, channel) p
              JOIN accounts a ON a.account_id = p.account_id JOIN parties x ON x.party_id = a.party_id JOIN currencies y ON y.currency_id = a.currency_id) k
  ASOF JOIN positions c
    MATCH_CONDITION c.update_time <= d.closes
    ON c.account_id = k.account_id AND c.channel = k.channel
  LEFT ASOF JOIN positions o
    MATCH_CONDITION o.update_time < d.opens
    ON o.account_id = k.account_id AND o.channel = k.channel
  ASOF JOIN prices m
    MATCH_CONDITION m.ts <= d.closes ON m.asset = k.asset
  ASOF JOIN rates q
    MATCH_CONDITION q.ts <= d.closes ON q.symbol = k.counter_symbol
  WHERE NOT (c.amt_signed = 0 AND c.cumulative_realized_pnl = 0 AND c.trade_count = COALESCE(o.trade_count, 0))
)
SELECT CASE WHEN GROUPING(asset) = 1 THEN '' ELSE asset END AS row_key, CASE WHEN GROUPING(leg) = 1 THEN '' ELSE leg END AS column_key,
       CASE WHEN MIN(asset) = MAX(asset) THEN SUM(qty) END AS quantity,
       SUM(notional) AS notional,
       SUM(realized) AS realized_pnl,
       SUM(unrealized) AS unrealized_pnl,
       SUM(balance) AS balance,
       SUM(volume) AS volume,
       SUM(trades) AS trades
FROM p
GROUP BY ROLLUP(asset), ROLLUP(leg)
