SELECT x.day_label, x.days_ago,
       x.open_local - CASE WHEN x.open_local < zo.starts + zo.previous_offset_minutes * INTERVAL '1' MINUTE THEN zo.previous_offset_minutes ELSE zo.utc_offset_minutes END * INTERVAL '1' MINUTE AS opens,
       CASE WHEN x.days_ago = 0 THEN x.now ELSE x.close_local - CASE WHEN x.close_local < zc.starts + zc.previous_offset_minutes * INTERVAL '1' MINUTE THEN zc.previous_offset_minutes ELSE zc.utc_offset_minutes END * INTERVAL '1' MINUTE END AS closes
FROM (SELECT n.now, n.zone, k.days_ago,
             n.close_local - k.days_ago * INTERVAL '1' DAY AS close_local,
             n.close_local - (k.days_ago + 1) * INTERVAL '1' DAY AS open_local,
             format_date(n.close_local - k.days_ago * INTERVAL '1' DAY - INTERVAL '1' MINUTE) AS day_label
      FROM (SELECT t.now, t.zone, CASE WHEN t.local_now < CAST(CAST(t.local_now AS DATE) AS TIMESTAMP(9)) + cutoff_hour * INTERVAL '1' HOUR THEN CAST(CAST(t.local_now AS DATE) AS TIMESTAMP(9)) + cutoff_hour * INTERVAL '1' HOUR
                                       ELSE CAST(CAST(t.local_now AS DATE) AS TIMESTAMP(9)) + INTERVAL '1' DAY + cutoff_hour * INTERVAL '1' HOUR END AS close_local
            FROM (SELECT s.now, s.zone, s.now + z.utc_offset_minutes * INTERVAL '1' MINUTE AS local_now
                  FROM (SELECT CURRENT_TIMESTAMP(9) AS now, zone_name AS zone) s
                  ASOF JOIN zones z MATCH_CONDITION z.starts <= s.now ON z.zone = s.zone) t) n
      CROSS JOIN (VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)) AS k (days_ago)) x
ASOF JOIN zones zc MATCH_CONDITION zc.local_starts <= x.close_local ON zc.zone = x.zone
ASOF JOIN zones zo MATCH_CONDITION zo.local_starts <= x.open_local ON zo.zone = x.zone
