-- Invariants on bloom_launch (BI-01, BI-03, idempotency). Each violation raises with the offending row; psql runs with ON_ERROR_STOP.
DO $$
DECLARE r record; n bigint;
BEGIN
  -- BI-01: no SKU is over its allocation, and the counters equal what the holds say.
  SELECT count(*) INTO n FROM launch_skus;
  SELECT * INTO r FROM launch_skus WHERE held + sold > allocation LIMIT 1;
  IF FOUND THEN RAISE EXCEPTION 'BI-01 launch_skus held+sold > allocation: %', r; END IF;

  SELECT s.launch_id, s.sku_id, s.allocation, s.held, s.sold, coalesce(q.qty, 0) AS recount INTO r
  FROM launch_skus s
  LEFT JOIN (SELECT l.sku_id, sum(l.quantity) AS qty FROM hold_lines l JOIN holds h ON h.id = l.hold_id
             WHERE h.status IN ('held', 'sold') GROUP BY l.sku_id) q ON q.sku_id = s.sku_id
  WHERE coalesce(q.qty, 0) > s.allocation OR coalesce(q.qty, 0) <> s.held + s.sold
  LIMIT 1;
  IF FOUND THEN RAISE EXCEPTION 'BI-01 hold_lines recount differs from launch_skus: %', r; END IF;
  RAISE NOTICE 'BI-01 ok: % launch SKUs checked', n;

  -- BI-03: no (account, product) is over its purchase limit, and limit_usage equals the recount from holds.
  SELECT count(*) INTO n FROM limit_usage;
  SELECT u.launch_id, u.account_id, u.product_id, u.held, u.sold, p.purchase_limit INTO r
  FROM limit_usage u JOIN launch_products p ON p.launch_id = u.launch_id AND p.product_id = u.product_id
  WHERE u.held + u.sold > p.purchase_limit LIMIT 1;
  IF FOUND THEN RAISE EXCEPTION 'BI-03 limit_usage held+sold > purchase_limit: %', r; END IF;

  WITH recount AS (
    SELECT h.launch_id, h.account_id, l.product_id, sum(l.quantity) AS qty
    FROM holds h JOIN hold_lines l ON l.hold_id = h.id
    WHERE h.status IN ('held', 'sold')
    GROUP BY h.launch_id, h.account_id, l.product_id)
  SELECT launch_id, account_id, product_id, coalesce(u.held + u.sold, 0) AS usage, coalesce(c.qty, 0) AS recount, p.purchase_limit INTO r
  FROM limit_usage u
  FULL JOIN recount c USING (launch_id, account_id, product_id)
  LEFT JOIN launch_products p USING (launch_id, product_id)
  WHERE coalesce(c.qty, 0) > p.purchase_limit OR coalesce(c.qty, 0) <> coalesce(u.held + u.sold, 0)
  LIMIT 1;
  IF FOUND THEN RAISE EXCEPTION 'BI-03 holds recount differs from limit_usage or limit: %', r; END IF;
  RAISE NOTICE 'BI-03 ok: % limit_usage rows checked', n;

  -- Idempotency: one hold per order.
  SELECT count(*) INTO n FROM holds;
  SELECT order_id, count(*) AS holds INTO r FROM holds GROUP BY order_id HAVING count(*) > 1 LIMIT 1;
  IF FOUND THEN RAISE EXCEPTION 'IDEMPOTENCY more than one hold for an order: %', r; END IF;
  RAISE NOTICE 'IDEMPOTENCY ok: % holds checked', n;
END $$;
