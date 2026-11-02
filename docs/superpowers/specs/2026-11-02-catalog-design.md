# Catalog service: design

| | |
|---|---|
| **Status** | Approved (2026-11-02) |
| **Date** | 2026-11-02 |
| **Branch / PR base** | `feat/catalog` → `feat/identity` |
| **Implements** | [08-catalog](../../../doc/low-level-design/08-catalog.md) (all sections), CAT-R01 … R15, REV-R01 … R08, WAR-R01, LCH-R13, HLD §5, §6, §7.1, HLD-ADR-014 (API/worker split), HLD-ADR-017 |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)), gateway ([api-gateway spec](2026-11-01-api-gateway-design.md)), identity contracts ([identity spec](2026-11-01-identity-design.md)) |

## 1. Intent

`catalog` defines what Bloom sells and at what price, serves fast public browsing and search, holds verified-buyer reviews, and answers order, launch and inventory synchronously (08-catalog §1). It is the price source for checkout and the owner of the serialized flag.

08-catalog is the approved, complete design. This spec does not restate it. It records only the owner decisions and builder calls the LLD leaves open. Where this spec is silent, 08-catalog decides.

## 2. Goals

1. Catalog **API** deployment: every method of 08-catalog §5.1, the three staff reads of §4, and the shared DLQ admin service (`MapBloomDeadLetterAdmin`, LLD-ADR-009).
2. Catalog **worker** deployment: outbox relay, the five consumers of §3, the daily pending-image cleanup (§6.7), the retention purger (from `Bloom.Shared`).
3. `identity.staff-action-recorded.v1` published through the outbox for every admin action.
4. Contracts other services need: catalog's internal `.proto` (`GetPricing`, `GetSku`, `LockSkuForStock`), and payload records for the messages catalog consumes that have none yet (§5).
5. The gateway route for customer review writes (§4).
6. Automated tests for C-01 … C-13 and every edge case of 08-catalog §8.

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case C-01 … C-13 has at least one test named after its ID.

## 3. Non-goals and anti-goals

**Non-goals:** the launch, order and inventory services (only the contracts listed in §5). Dockerfile, Compose entries, Kubernetes manifests. Any change to `Bloom.Shared`. Any gateway change beyond the one route in §4. Any change under `doc/` (proposed edits in §9). CR-01 p95 is not asserted; it's deferred to the cluster load test.

**Anti-goals** (failure even if it technically works):
- Any method, message, field or error reason that 08-catalog or this spec doesn't list (AGENTS.md §1).
- `GetPricing`, `GetSku` or `LockSkuForStock` reading from the cache (CR-02).
- A public endpoint returning a non-visible product, a plan product on its own, an inactive SKU, a `pending` image or a hidden review (§9).
- `serialized` changing after `serialized_locked` (CR-03), or any hard delete of a product (CR-06).
- `author_display` taken from the request instead of `reviewer_names` (§6.5).
- Raw HTML from staff Markdown reaching `description_html`.

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of 08-catalog, nothing more except the items in this table. Code + tests only. |
| Review write route | New gateway route `/api/reviews/{**rest}` → catalog, `Auth: customer`, `Policy: user`, timeout 5 s (same shape as `cart`). Added to the gateway's `appsettings.json` with a routing test. `UpsertMyReview` and `DeleteMyReview` live under it. Doc edit §9.1. |
| Staff reads | Three new admin methods under `/api/admin/catalog/**`. **`AdminListProducts`**: any status; filters `status?`, `category_id?`, `q?` (name/slug/SKU code contains, case-insensitive); paged (`page`, `page_size ≤ 100`), newest-updated first. **`AdminGetProduct`** (`id`): everything, including Markdown source, inactive SKUs, images in both statuses, plan eligibility, `serialized_locked`, `version`. **`AdminListReviews`**: filters `product_id?`, `status?`; paged; newest first; includes `hidden_by`, `hidden_reason`. Never cached. Doc edit §9.2. |
| Warranty plans on the device page | `GetProduct` of a physical product returns `warranty_plans[]`: each eligible plan whose product is visible and whose SKU is active, with plan `product_id`, `name`, `coverage_months`, `sku_id`, `price_cents`. Plans don't appear in `SearchProducts` results or category listings. Doc edit §9.3. |
| Plans have no public page and no reviews | Public `GetProduct` (id or slug) of a plan → `NOT_FOUND`; a plan exists publicly only inside a device's `warranty_plans[]`. The `order.delivered.v1` consumer skips plan product ids (no `review_eligibility`). `UpsertMyReview` on a plan → `NOT_FOUND`. Plans carry no rating. Admin reads and `GetPricing` still see plans. Doc edit §9.9. |
| Search option filter | Values of the **same option type are ORed, different types ANDed, and all on the same active SKU**. Storage {256, 512} + Color {Black} matches a product with an active SKU that is (256 or 512) and Black. Doc edit §9.10. |
| Review `version` | `UpsertMyReview` **without** `version` = create; if the customer already has a review on that product → `CONCURRENCY_CONFLICT` (this is C-09's rejection). **With** `version` = edit with optimistic locking: no review → `NOT_FOUND`; stale version → `CONCURRENCY_CONFLICT`; identical rating and text → success, nothing written (version, aggregates and cache unchanged). Doc edit §9.11. |
| Deleted account vs late events | Race accepted. A late `customer-registered` / `contact-changed` or `order.delivered.v1` processed after `identity.account-deleted.v1` may re-create a `reviewer_names` / `review_eligibility` row for the deleted account. No tombstone table. |
| Error reasons beyond §5.3 | Reused from identity: `NOT_FOUND` (`NOT_FOUND`) for an unknown product / SKU / category / image / review, a public read of a non-visible product, or editing / deleting a review the caller doesn't have; `VALIDATION_FAILED` (`INVALID_ARGUMENT`, field name in `ErrorInfo` metadata) for a bad field, e.g. rating outside 1–5, text > 2,000, price ≤ 0, plan eligibility pointing at a non-physical product. New: `SLUG_TAKEN` (`ALREADY_EXISTS`) for a category or product slug in use; `SKU_CODE_TAKEN` (`ALREADY_EXISTS`) for a SKU code in use. Shared from 00-reliability: `CONCURRENCY_CONFLICT`, `ROLE_NOT_ALLOWED`. Doc edit §9.4. |
| Reviews on non-visible products | `UpsertMyReview` needs the product to be visible (08-catalog §6.1); otherwise `NOT_FOUND`. `DeleteMyReview` always works. Existing reviews on an archived product stay and show again when it's reactivated. Doc edit §9.5. |
| "First L." from identity's `name` | Trim, split on whitespace: first word + space + first letter of the last word + `.` (`Mary Jane Watson` → `Mary W.`). One word → that word (`Cher`). Empty → `Bloom customer` (08-catalog §8 fallback). Doc edit §9.6. |
| Description Markdown | Catalog renders on write with **Markdig**, raw HTML disabled, and stores the result in a new column `products.description_html`. Public `GetProduct` returns `description_html` only; admin reads return `description_md` too. Doc edit §9.7. |
| Object storage client | **AWSSDK.S3**, configured with a service URL and path-style addressing for MinIO. Tests use a MinIO Testcontainer. |
| Branch | `feat/catalog` off `feat/identity`, PR into `feat/identity`. |

## 5. Layout and dependencies (builder's call)

```
src/Catalog/
  Bloom.Catalog/            # one ASP.NET Core app; runs as catalog-api or catalog-worker by config
  Bloom.Catalog.Contracts/  # catalog_internal.proto (client + server stubs) for order, launch, inventory
  Bloom.Catalog.Tests/      # xUnit v3 + Testcontainers (PostgreSQL, Redis, RabbitMQ, MinIO)
src/Launch/
  Bloom.Launch.Contracts/   # new: LaunchStageChangedV1 payload record (01-launch §5.2)
src/Order/
  Bloom.Order.Contracts/    # existing: add OrderDeliveredV1 payload record (03-order §5.2)
```

- One app with a mode setting (`api` / `worker`), like identity (HLD-ADR-014).
- `AddBloom<CatalogDbContext>` provides outbox, inbox, consumer host, retention purger, error mapping, metrics; `AddBloomTelemetry` for OTel.
- EF Core migrations in `Bloom.Catalog`: the tables of 08-catalog §4.1 plus `description_html`, plus `AddBloomReliabilityTables`.
- New packages: `AWSSDK.S3`, `Markdig`, `Testcontainers.Minio`. Everything else is already pinned.
- Identity payload records come from the existing `Bloom.Identity.Contracts`; the staff-action payload is the existing `Bloom.Shared.DeadLetters.StaffActionRecorded`.
- All new projects added to `Bloom.slnx`.

## 6. Routes (builder's call)

gRPC services transcoded to JSON; paths sit under the gateway prefixes of LLD 07 §5.1 plus the new `/api/reviews/**`.

| Gateway prefix (auth) | Methods → path |
|---|---|
| `GET /api/catalog/**` (public) | `ListCategories` `/api/catalog/categories` · `SearchProducts` `/api/catalog/products` (query string) · `GetProduct` `/api/catalog/products/{id}` and `/api/catalog/products/by-slug/{slug}` · `ListReviews` `/api/catalog/products/{product_id}/reviews` |
| `/api/reviews/**` (customer) | `UpsertMyReview` `PUT /api/reviews/{product_id}` · `DeleteMyReview` `DELETE /api/reviews/{product_id}` |
| `/api/admin/catalog/**` (staff) | Categories, products, options, SKUs, prices, plans, images, lifecycle (Catalog manager, Admin) and the three staff reads; `HideReview` / `UnhideReview` / `AdminListReviews` (Support, Admin). Paths under `/api/admin/catalog/{categories,products,skus,images,reviews}/…`. |
| internal, not routed | `GetPricing`, `GetSku`, `LockSkuForStock` (plain gRPC, `Bloom.Catalog.Contracts`) |

- Per-method role checks from the trusted context (LLD 07 §4.3). Refusal → `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`. `AdminListProducts` / `AdminGetProduct`: Catalog manager, Admin (my call).
- Updates carry `version`; a mismatch → `CONCURRENCY_CONFLICT` (00-reliability §7.6).

## 7. Implementation notes (builder's calls unless marked)

- **Visibility** follows the 08-catalog §6.1 table exactly. A product with `launch_stage` in (`announced`, `preorder_open`) is visible whatever its `status`; `draft` launch stage hides it; `released` or no launch → visible iff `status = active`. SKUs shown publicly must also be `active`.
- **`GetPricing` `kind`** uses the wire values of 03-order §5 (`product` \| `warranty_plan`); catalog stores `physical` and maps it.
- **Warranty plans** have no option types and exactly one SKU (empty `option_key`). `SetOptions` on a plan → `VALIDATION_FAILED`; a second plan SKU → `DUPLICATE_COMBINATION` via the unique key.
- **`GetSku` / `LockSkuForStock` with an unknown SKU** → gRPC `NOT_FOUND` / `NOT_FOUND` (within the approved reason set). Doc edit §9.12.
- **Serialized flag** is changed through `UpdateProduct`; the write is the guarded update of 08-catalog §7 (`WHERE serialized_locked = false`), else `SERIALIZED_LOCKED`.
- **`search_vector`** is maintained by the app on every write that changes its inputs (name, option values, category name, description). A Postgres generated column can't read other tables. Doc edit §9.8.
- **`min_price_cents`** recomputed in the same transaction as any SKU price or `active` change; null when no active SKU.
- **Cache** keys and invalidation exactly as 08-catalog §6.3. A plan price, plan status or eligibility change also deletes `product:{id}` of every eligible device (their `warranty_plans[]`). `GetProduct` by slug resolves the id from Postgres, then reads `product:{id}`.
- **Images.** A presigned `PUT` can't cap size, so the 5 MB limit is enforced at `ConfirmImage`: `HEAD` size > 5 MB or missing object → `IMAGE_INVALID`, the object is deleted, and the row stays `pending` for the daily job (C-13). Public image URL = configured public base URL + `storage_key`.
- **Launch events.** Stage-order guard of §6.4 on the product row; SKU `launch_id` set from `skus[]`.
- **Consumers** use the shared inbox; queue names and bindings per 00-reliability §6.
- **Audit.** Every admin write adds a `StaffActionRecorded` outbox row in its transaction; `service` = `catalog`, `action` = `catalog.<event>` (e.g. `catalog.price-changed`, `catalog.product-archived`, `catalog.review-hidden`), `details` holds before / after where relevant.
- **Cleanup job** (worker, daily): delete `pending` images older than 24 h and their objects (§6.7).
- **Metrics** of 08-catalog §10 on meter `Bloom.Catalog`.

## 8. Testing

- xUnit v3 + Testcontainers: real PostgreSQL, Redis, RabbitMQ and MinIO (one container each per run). The app runs in-process (`WebApplicationFactory`), driven over transcoded HTTP and gRPC where simpler.
- Redis-down test for the 08-catalog §6.3 fallback (reads still succeed from Postgres).
- `FakeTimeProvider` for the 24 h image cleanup.
- Gateway: one routing test for `/api/reviews/**` (customer allowed; anonymous and staff refused).
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). For the owner to approve separately:

1. 07-api-gateway §5.1: add `/api/reviews/**` → catalog, Customer, `user`. 08-catalog §3: Customer API served there.
2. 08-catalog §5.1: add `AdminListProducts`, `AdminGetProduct`, `AdminListReviews` as in §4.
3. 08-catalog §5.1 `GetProduct`: add `warranty_plans[]` for physical products; §6.2: plans are excluded from search and listings.
4. 08-catalog §5.3: add `NOT_FOUND`, `VALIDATION_FAILED`, `SLUG_TAKEN`, `SKU_CODE_TAKEN`.
5. 08-catalog §6.5: write / edit need a visible product; delete always allowed.
6. 08-catalog §4.1 `reviewer_names`: the "First L." derivation of §4.
7. 08-catalog §4.1 `products`: add `description_html` (rendered on write, Markdig, raw HTML disabled); §9 wording to match.
8. 08-catalog §4.1 `search_vector`: "maintained on write" instead of "generated".
9. 08-catalog §6.1 / §6.5: warranty plans have no public page and are not reviewable; the `order.delivered.v1` consumer skips plan products. 09-reviews: a line saying plans aren't reviewed.
10. 08-catalog §6.2: option filter = OR within a type, AND across types, on the same active SKU.
11. 08-catalog §5.1 `UpsertMyReview`: the `version` rules of §4; §11 C-09 expected result: `CONCURRENCY_CONFLICT`.
12. 08-catalog §5.1 `GetSku`, `LockSkuForStock`: unknown SKU → `NOT_FOUND`.

## 10. Left to the builder

Internal names, class and proto layout (package `bloom.catalog.v1`, one gRPC service per 08-catalog §3 component), request / response message shapes beyond the fields 08-catalog §5.1 and this spec name, test isolation, package versions (latest stable compatible with .NET 10). Anything visible outside catalog or affecting the design that 08-catalog and this spec don't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

None.
