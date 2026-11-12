# LLD — catalog service

[Docs](../README.md) › [Low-level design](README.md)

| | |
|---|---|
| **Status** | Approved (2026-11-12) |
| **Last updated** | 2026-10-31 |
| **Product rules** | [02-catalog](../product-design/02-catalog.md) CAT-R01 … R15, [09-reviews](../product-design/09-reviews.md) REV-R01 … R08, WAR-R01, LCH-R13 |
| **HLD** | [high-level-design.md §5, §6](../high-level-design/high-level-design.md), HLD-ADR-017 |
| **Depends on** | [00-reliability.md](00-reliability.md), [01-launch.md](01-launch.md) (stage events) |

## 1. Problem and scope

`catalog` defines what Bloom sells and at what price, serves fast public browsing and search, and holds verified-buyer reviews. It is the price source for checkout and the owner of the serialized flag.

**In scope:** categories, products and lifecycle, option types and values, SKUs and prices, warranty plans and eligibility, product images, full-text search, reviews and ratings, launch membership copy, the read cache.

**Out of scope:** stock (`inventory`), launch allocation (`launch`), order snapshots (`order` stores its own copy at checkout).

## 2. Requirements and constraints

| ID | Requirement | Source |
|---|---|---|
| CR-01 | Catalog reads p95 < 150 ms at launch peak. | QA-02 |
| CR-02 | `GetPricing` always returns current prices from Postgres (never cached). | CAT-R08, OR-06 |
| CR-03 | The serialized flag can't change once locked by the first goods receipt. | CAT-R14 |
| CR-04 | Product pages reflect a price change within seconds. | Owner decision (delete on write) |
| CR-05 | Only verified buyers review, once per product; hidden reviews don't count. | REV-R01 … R06 |
| CR-06 | A sold product is never hard-deleted. | CAT-R13 |

## 3. Component structure

| Component | Deployment | Responsibility |
|---|---|---|
| Public API | API | Categories, search, product detail, reviews list |
| Customer API | API | Write / edit / delete own review (served under `/api/reviews/**`) |
| Admin API | API | Categories, products, options, SKUs, prices, plans, images, lifecycle, review moderation |
| Internal API | API | `GetPricing` (order), `GetSku` (launch), `LockSkuForStock` (inventory) |
| Consumers | Worker | `launch.stage-changed.v1`, `order.delivered.v1`, `identity.customer-registered.v1`, `identity.contact-changed.v1`, `identity.account-deleted.v1` |
| Reliability plumbing | Both | Outbox (audit events), inbox ([00-reliability.md](00-reliability.md)) |

## 4. Data model

### 4.1 PostgreSQL (catalog database)

**`categories`**: `id`, `parent_id` (null = top level; a parent must itself be top level, CAT-R01), `name`, `slug` (unique), `sort_order`, `version`.

**`products`**

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` | PK |
| `category_id` | `uuid` | FK, top or sub level |
| `kind` | `text` | `physical` \| `warranty_plan` (CAT-R06) |
| `name`, `slug` | `text` | `slug` unique |
| `description_md` | `text` | Markdown, ≤ 20,000 chars |
| `description_html` | `text` | Rendered from `description_md` on write (Markdig, raw HTML disabled) |
| `specs` | `jsonb` | `[{group, items: [{label, value}]}]`, display-only |
| `serialized` | `bool` | physical only |
| `serialized_locked` | `bool` | set by `LockSkuForStock` (CR-03) |
| `coverage_months` | `int` | warranty plans only |
| `status` | `text` | `draft` \| `active` \| `archived` (CAT-R12) |
| `launch_id`, `launch_stage` | `uuid`, `text` | copy from launch events; null if not a launch product |
| `published_at` | `timestamptz` | first activation (sort "newest") |
| `min_price_cents` | `bigint` | lowest price among active SKUs (maintained on write) |
| `rating_sum`, `rating_count` | `int` | visible reviews only (CAT-R10) |
| `search_vector` | `tsvector` | maintained on write: name (A), option values (B), category name (B), description (C); English config |
| `version` | `bigint` | |

Indexes: GIN on `search_vector`; `(category_id, status)`; `(min_price_cents)`; `(published_at)`.

**`option_types`**: `id`, `product_id`, `name`, `position`. **`option_values`**: `id`, `option_type_id`, `value`, `position`.

**`skus`**

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` | PK |
| `product_id` | `uuid` | FK |
| `code` | `text` | unique human code (for example `IP17P-256-BLK`) |
| `option_key` | `text` | sorted option value IDs; `UNIQUE (product_id, option_key)` (CAT-R03) |
| `price_cents` | `bigint` | > 0 |
| `active` | `bool` | inactive = hidden, not buyable, kept for history |
| `launch_id` | `uuid` | copy from launch events |
| `version` | `bigint` | |

**`sku_option_values`**: `(sku_id, option_value_id)`.

**`plan_eligibility`**: `(plan_product_id, eligible_product_id)` (CAT-R07).

**`product_images`**: `id`, `product_id`, `sku_id` (null = all SKUs), `storage_key`, `alt_text`, `position`, `status` (`pending` \| `ready`).

**`reviews`**

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` | PK |
| `product_id` | `uuid` | REV-R02 (per product) |
| `account_id` | `uuid` | null after account deletion |
| `author_display` | `text` | snapshot "First L."; `Former customer` after deletion (REV-R08) |
| `rating` | `smallint` | 1–5 |
| `text` | `text` | null, ≤ 2,000 chars (REV-R04) |
| `status` | `text` | `visible` \| `hidden` |
| `hidden_by`, `hidden_reason` | | Support |
| `version` | `bigint` | |
| `created_at`, `updated_at` | | |

`UNIQUE (product_id, account_id) WHERE account_id IS NOT NULL` (REV-R03).

**`review_eligibility`**: PK `(account_id, product_id)`, `first_delivered_at` (REV-R01).
**`reviewer_names`**: PK `account_id`, `display_name` ("First L."), `updated_at`, from identity events. `display_name` derives from identity's `name`: trim, split on whitespace: first word + space + first letter of the last word + `.` (`Mary Jane Watson` → `Mary W.`). One word → that word (`Cher`). Empty → `Bloom customer` (§8 fallback). `updated_at` is set from the event's `OccurredAt`; an upsert applies only if it is newer (the newest event wins).

### 4.2 Redis (catalog's own instance)

| Key | Content | TTL |
|---|---|---|
| `product:{id}` | Product detail view (incl. SKUs, images, rating) | 10 min |
| `categories` | Category tree | 10 min |
| `search:{catalogVersion}:{hash(params)}` | One result page | 10 min |
| `catalog:version` | Integer, incremented by any write that changes listings | none |

### 4.3 Object storage

Bucket `bloom-product-images` (MinIO locally, S3-compatible in the cloud), public read. Key `products/{productId}/{uuid}.{ext}`.

## 5. Interfaces

### 5.1 gRPC

| Method | Caller | Request → Response |
|---|---|---|
| `ListCategories` | Public | → tree |
| `SearchProducts` | Public | `q?`, `category_id?`, `price_min?`, `price_max?`, `option_value_ids[]?`, `sort` (`relevance` \| `price_asc` \| `price_desc` \| `newest` \| `rating`), `page`, `page_size ≤ 48` → products with min price, rating, first image |
| `GetProduct` | Public | `slug` or `id` → product, SKUs (active, visible), options, images, specs, rating, launch info; for a physical product also `warranty_plans[]`: each eligible plan whose product is visible and whose SKU is active, with plan `product_id`, `name`, `coverage_months`, `sku_id`, `price_cents` |
| `ListReviews` | Public | `product_id`, `page` → visible reviews, newest first |
| `UpsertMyReview` / `DeleteMyReview` | Customer | `product_id`, `rating`, `text?`, `version?`. `UpsertMyReview` without `version` = create; if the customer already has a review on that product → `CONCURRENCY_CONFLICT`. With `version` = edit with optimistic locking: no review → `NOT_FOUND`; stale version → `CONCURRENCY_CONFLICT`; identical rating and text → success, nothing written (version, aggregates and cache unchanged). |
| `GetPricing` | order | `sku_ids[]` → per SKU: `price_cents`, `name`, `product_id`, `category_id`, `kind`, `serialized`, `buyable`, `launch {launch_id, stage}?`, plans: `eligible_product_ids`, `coverage_months` |
| `GetSku` | launch | `sku_id` → `product_id`, `status`, `active`, `launch_id`; unknown SKU → `NOT_FOUND` |
| `LockSkuForStock` | inventory | `sku_id` → `product_id`, `serialized` (sets `serialized_locked`); unknown SKU → `NOT_FOUND` |
| Admin: `Create/UpdateCategory`, `Create/UpdateProduct`, `SetOptions`, `Create/UpdateSku`, `SetSkuPrice`, `SetSkuActive`, `SetPlanEligibility`, `PublishProduct`, `ArchiveProduct`, `ReactivateProduct`, `CreateImageUpload`, `ConfirmImage`, `ReorderImages`, `DeleteImage` | Catalog manager, Admin | optimistic `version` on updates |
| `HideReview` / `UnhideReview` | Support, Admin | `review_id`, `reason` |
| `AdminListProducts` | Catalog manager, Admin | any status; filters `status?`, `category_id?`, `q?` (name/slug/SKU code contains, case-insensitive); paged (`page`, `page_size ≤ 100`), newest-updated first. Never cached. |
| `AdminGetProduct` | Catalog manager, Admin | `id` → everything, including Markdown source, inactive SKUs, images in both statuses, plan eligibility, `serialized_locked`, `version`. Never cached. |
| `AdminListReviews` | Support, Admin | filters `product_id?`, `status?`; paged; newest first; includes `hidden_by`, `hidden_reason`. Never cached. |

### 5.2 Messages

| Message | Direction | Effect |
|---|---|---|
| `launch.stage-changed.v1` | Consumed | §6.4 |
| `order.delivered.v1` | Consumed | Upsert `review_eligibility` for each `productId` |
| `identity.customer-registered.v1` / `contact-changed.v1` | Consumed | Upsert `reviewer_names`; `updated_at` = the event's `OccurredAt`, applied only if newer (a `contact-changed` applied before `customer-registered` keeps the newer name) |
| `identity.account-deleted.v1` | Consumed | §6.6 |
| `identity.staff-action-recorded.v1` | Published | Every admin action (prices, lifecycle, moderation) |

### 5.3 Error reasons

| Reason | Status | When |
|---|---|---|
| `SERIALIZED_LOCKED` | `FAILED_PRECONDITION` | Changing `serialized` after lock (CAT-R14) |
| `DUPLICATE_COMBINATION` | `ALREADY_EXISTS` | SKU with an existing option combination |
| `CATEGORY_DEPTH` | `INVALID_ARGUMENT` | Third category level |
| `INVALID_LIFECYCLE` | `FAILED_PRECONDITION` | Disallowed product status change |
| `PUBLISH_INCOMPLETE` | `FAILED_PRECONDITION` | Publish without an active SKU with a price |
| `NOT_VERIFIED_BUYER` | `PERMISSION_DENIED` | Review without eligibility (REV-R01) |
| `IMAGE_INVALID` | `INVALID_ARGUMENT` | Wrong type, too large, or object missing at confirm |
| `NOT_FOUND` | `NOT_FOUND` | Unknown product / SKU / category / image / review, a public read of a non-visible product, or editing / deleting a review the caller doesn't have |
| `VALIDATION_FAILED` | `INVALID_ARGUMENT` | Bad field (for example rating outside 1–5, text > 2,000, price ≤ 0, plan eligibility pointing at a non-physical product); field name in `ErrorInfo` metadata |
| `SLUG_TAKEN` | `ALREADY_EXISTS` | Category or product slug in use |
| `SKU_CODE_TAKEN` | `ALREADY_EXISTS` | SKU code in use |

## 6. Behaviour

### 6.1 Product lifecycle and visibility

```mermaid
stateDiagram-v2
    [*] --> draft
    draft --> active : PublishProduct (≥ 1 active SKU with price)
    active --> archived : ArchiveProduct
    archived --> active : ReactivateProduct (CAT-R12)
```

| Condition | Visible on site | `buyable` (GetPricing) |
|---|---|---|
| No launch, `status = active`, SKU `active` | Yes | Yes |
| `status = draft` / `archived`, or SKU inactive | No | No |
| Launch SKU, stage `draft` | No | No |
| Launch SKU, stage `announced` | Yes | No |
| Launch SKU, stage `preorder_open` | Yes | No for regular checkout; `launch` info returned so order can run the launch flow |
| Launch SKU, stage `released` | As a regular product (product set `active`, CAT-R11) | Yes |

Products are never hard-deleted (CR-06); archiving is the removal path.

Warranty plans have no public page: a public `GetProduct` (id or slug) of a plan → `NOT_FOUND`. A plan exists publicly only inside a device's `warranty_plans[]`. Plans carry no rating. Admin reads and `GetPricing` still see plans.

### 6.2 Search (HLD-ADR-017)

- Text: `websearch_to_tsquery('english', q)` against `search_vector`; rank with `ts_rank_cd`.
- Filters: category (a top-level category includes its sub-categories); price on `min_price_cents`; option values of the same option type are ORed, different types ANDed, all on the same active SKU (storage {256, 512} + color {Black} matches a product with an active SKU that is (256 or 512) and Black).
- Warranty plans are excluded from search results and category listings.
- Sorts: relevance, `min_price_cents` asc/desc, `published_at` desc, rating (`rating_sum / rating_count` desc, then count desc; products without reviews last).
- Only visible products (§6.1). Offset pagination (catalog size is a few hundred products).

### 6.3 Cache (cache-aside, delete on write)

- **Read:** `GET` key → hit: return; miss: load from Postgres, `SET` with TTL 10 min.
- **Write** (after commit): `DEL product:{id}` for each affected product; `DEL categories` for category changes; `INCR catalog:version` for anything affecting listings (price, status, rating, images, options, launch stage). Search keys include the version, so old pages simply stop being read and expire.
- Redis unavailable → read from Postgres directly (degraded latency, correct data).
- `GetPricing`, `GetSku`, `LockSkuForStock` never use the cache (CR-02).

### 6.4 Launch stage events

On `launch.stage-changed.v1`: set `launch_id` on the listed SKUs and `launch_id`, `launch_stage` on their products; bump the cache. On `stage = released`: set the products `active` (and `published_at` if null). Events are applied with a stage-order guard (`draft < announced < preorder_open < released`), so a late older event is a no-op. Archived is final for launch events: the launch consumer never changes an `archived` product; only staff can un-archive (`ReactivateProduct`).

### 6.5 Reviews

| Action | Rules |
|---|---|
| Write | The product must be visible (§6.1), else `NOT_FOUND`; a warranty plan is not reviewable (`NOT_FOUND`). Customer must have `review_eligibility (account, product)` (REV-R01; returns don't remove it). One per product (REV-R03). `author_display` copied from `reviewer_names` (never from the request). Visible immediately (REV-R05). Same transaction: `rating_sum += r`, `rating_count += 1`. |
| Edit | Own review; the product must be visible (§6.1); `rating_sum += new − old` if visible. |
| Delete | Own review; always allowed, even if the product is not visible; row deleted; aggregate decreased if visible. A new review may be written later. |
| Hide / unhide | Support; aggregate decreased / increased (REV-R06). The author isn't notified. |

All aggregate changes are in the same transaction as the review change, on the product row.

The `order.delivered.v1` consumer skips warranty-plan product ids (no `review_eligibility`).

### 6.6 Account deleted

`reviews` of the account: `account_id = null`, `author_display = 'Former customer'` (REV-R08), reviews stay visible. Delete `review_eligibility` and `reviewer_names` rows for the account.

### 6.7 Images

1. `CreateImageUpload(product_id, sku_id?, content_type)`: type in `image/jpeg`, `image/png`, `image/webp`; insert `product_images` (`pending`); return a presigned `PUT` URL (5 min, max 5 MB, exact content type).
2. Staff upload directly to storage.
3. `ConfirmImage(image_id)`: `HEAD` the object (exists, size ≤ 5 MB) → `ready`; bump the cache.
4. A daily job deletes `pending` rows older than 24 h and their objects.

### 6.8 Internal calls

- **`GetPricing`:** batch read from Postgres; unknown SKU IDs are returned as `buyable = false` with no price.
- **`LockSkuForStock`:** `UPDATE products SET serialized_locked = true WHERE id = (SELECT product_id FROM skus WHERE id = @s)`; returns the flag (idempotent).
- **`GetSku`:** used by launch when staff add a SKU to a launch.

## 7. Concurrency and consistency

| Risk | Protection |
|---|---|
| Two staff edit a product | Optimistic `version` → 409 |
| Flag change vs first receipt | `SetSerialized` is a guarded update `WHERE serialized_locked = false` |
| Concurrent reviews by the same customer | Unique index |
| Rating drift | Aggregate updated in the review's transaction |
| Stale cache after a write | Delete after commit + TTL safety net + versioned search keys |
| Reordered launch events | Stage-order guard |

## 8. Edge cases

| Case | Expected behaviour |
|---|---|
| Price changed during someone's checkout | Their locked price stands (order); new checkouts use the new price. |
| Last active SKU deactivated | Product shows as unavailable; still visible if `active` (no buyable SKU). |
| Archived product with stock | Not buyable; Warehouse adjusts stock (CAT-R13). |
| Customer renames themselves | Existing reviews keep the snapshot name; new reviews use the new name. |
| Review written before the name event arrives | `author_display = 'Bloom customer'` fallback. |
| Image upload never confirmed | Cleaned after 24 h. |
| Launch event for an `archived` product | The product is not changed; only staff can un-archive (§6.4). |
| Search query with only stop words | Empty text filter; returns filtered/sorted results. |

## 9. Security and validation

- Public endpoints return only visible products and visible reviews.
- Review text is stored as plain text and HTML-escaped by clients; no Markdown or links in reviews.
- Product Markdown is authored by staff only; rendered on write with Markdig, raw HTML disabled, and stored in `description_html`.
- Presigned URLs are scoped to one key, one content type, 5 minutes.
- Staff methods check roles; all admin actions are audited.

## 10. Observability

| Metric | Labels | Purpose |
|---|---|---|
| `catalog_read_seconds` | `endpoint`, `cache` = `hit` \| `miss` | CR-01 |
| `catalog_cache_hit_ratio` | `kind` | Cache health |
| `search_seconds` | — | Search latency |
| `get_pricing_seconds` | — | Checkout dependency |
| `reviews_total` | `action` | Review activity |

**Alerts:** read p95 > 150 ms; `get_pricing` p95 > 50 ms; cache hit ratio < 80% during a launch.

## 11. Test cases

| # | Case | Expected |
|---|---|---|
| C-01 | Change `serialized` after `LockSkuForStock` | `SERIALIZED_LOCKED` |
| C-02 | Third-level category | `CATEGORY_DEPTH` |
| C-03 | Duplicate option combination | `DUPLICATE_COMBINATION` |
| C-04 | Price change, then product page read | New price (cache deleted) |
| C-05 | `GetPricing` right after a price change | New price (no cache) |
| C-06 | Launch SKU in `announced` | Visible, `buyable = false` |
| C-07 | Launch released | Product `active`, regular buyable |
| C-08 | Review without delivery | `NOT_VERIFIED_BUYER` |
| C-09 | Second review on the same product | `CONCURRENCY_CONFLICT` |
| C-10 | Hide a 5-star review on a 2-review product averaging 4 | Average becomes 3 |
| C-11 | Account deleted | Reviews show "Former customer", still counted |
| C-12 | Search "pro max 256" with storage filter | Matching products only |
| C-13 | Upload a 6 MB image | Rejected by storage / `IMAGE_INVALID` |

## 12. Open questions

| ID | Question |
|---|---|
| — | None open. |
