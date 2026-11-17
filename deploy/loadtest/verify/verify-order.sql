-- Idempotency on bloom_order: every launch order has exactly one checkout_keys row (one Idempotency-Key, one order).
DO $$
DECLARE r record; n bigint;
BEGIN
  SELECT count(*) INTO n FROM orders WHERE launch_id IS NOT NULL;
  SELECT o.id AS order_id, count(k.order_id) AS checkout_keys INTO r
  FROM orders o LEFT JOIN checkout_keys k ON k.order_id = o.id
  WHERE o.launch_id IS NOT NULL
  GROUP BY o.id HAVING count(k.order_id) <> 1 LIMIT 1;
  IF FOUND THEN RAISE EXCEPTION 'IDEMPOTENCY launch order without exactly one checkout key: %', r; END IF;
  RAISE NOTICE 'IDEMPOTENCY ok: % launch orders checked', n;
END $$;
