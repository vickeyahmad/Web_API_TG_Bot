# Development Progress

## Current Phase: Milestone 4A — Central Payment System Foundation ✅ COMPLETE

---

## Completed

### Foundation Setup (Milestone 1)
- Development tools installed (dotnet 10, Docker, Git)
- Docker tested and running
- Local Git initialized and connected to GitHub
- `.env` excluded from Git via `.gitignore`
- `.env.example` committed as safe reference
- ASP.NET Core 10 solution created (`WebApiTgBot.slnx`)
- Backend project created at `src/WebApiTgBot/`
- Automated test project created at `tests/WebApiTgBot.Tests/`
- Modular folder structure created under `src/WebApiTgBot/Modules/`
- PostgreSQL running in Docker with persistent volume
- Entity Framework Core configured with Npgsql PostgreSQL provider
- Database connection secured via `dotnet user-secrets` (local)
- Initial EF Core migration created and applied to PostgreSQL
- `/api/health` endpoint implemented (returns DB status)
- Swagger/OpenAPI configured (available at `/swagger` in Development)
- 3 automated integration tests passing (using in-memory DB)
- `dotnet-ef` tool installed globally

### Milestone 2A — Central Product Catalog and Vendor Mapping
- **Domain Models**: `Category`, `Product`, `Vendor`, `VendorProduct`
- **EF Core Configurations**: Explicit decimal precision `decimal(18,4)`, unique indexes on `Category.Slug` and `Product.InternalSku`, composite unique index on `VendorProduct(VendorId, ProductId)`, performance indexes.
- **Multi-Currency Readiness**: `SellingCurrency` (default `PKR`) vs `VendorCostCurrency` (`PKR`, `USD`, `USDT`).
- **Service Layer**: `ICatalogService` and `CatalogService`.
- **Public Read-Only DTOs**: `CategoryDto`, `ProductSummaryDto`, `ProductDetailDto`, `PagedResult<T>`. Vendor details strictly excluded.
- **Public API Endpoints**: `GET /api/categories`, `GET /api/products`, `GET /api/products/{sku}`.
- **Database Migration**: `20261011205814_AddCatalogAndVendorMapping` applied to PostgreSQL.

### Milestone 2B-1 — Secure Admin Authentication
- **Central Identity Architecture**:
  - `ApplicationUser` (`Id`, `UserName`, `Email`, `FullName`, `IsActive`, `CreatedAt`, `UpdatedAt`) extending `IdentityUser`.
  - `ApplicationRole` extending `IdentityRole` with predefined roles in `AppRoles` (`Admin`, `Customer`).
  - `AppDbContext` upgraded to inherit `IdentityDbContext<ApplicationUser, ApplicationRole, string>`.
- **Security & Authentication Features**:
  - Secure PBKDF2/Argon2 password hashing via ASP.NET Core Identity.
  - HTTP-only authentication cookie (`.WebAPI.Admin.Auth`) configured with `SameSite = Lax` (for cross-subdomain requests from `admin.gogaabyte.com` to `api.gogaabyte.com`), `SecurePolicy = Always` (in production), and 8-hour sliding expiration.
  - Web API event overrides: returns HTTP `401 Unauthorized` and `403 Forbidden` instead of HTML redirects.
  - **Preserved Security Stamp Validation & Instant Session Revocation**: Built-in `SecurityStampValidator.ValidatePrincipalAsync` is explicitly preserved inside `OnValidatePrincipal` with `ValidationInterval = TimeSpan.Zero`. Password resets, demotions, or account deactivations immediately revoke active sessions on the very next request.
  - **Brute-Force Protection**: Fixed window rate limiter on login endpoint + Identity account lockout (15-minute lockout after 5 consecutive failed attempts).
  - **Centralized CSRF / Antiforgery Protection**: Globally registered `AutoValidateAntiforgeryTokenAttribute` protects all state-changing endpoints (POST/PUT/PATCH/DELETE) across all controllers. Client request token securely delivered via protected, non-cacheable JSON response from `GET /api/admin/auth/csrf` while `.WebAPI.Antiforgery` cookie remains `HttpOnly = true`.
  - **Explicit & Restrictive CORS**: Configured for `https://admin.gogaabyte.com` and `https://api.gogaabyte.com` in production (and `http://localhost:3000` in dev) with `.AllowCredentials()`. Wildcards are strictly prohibited.
  - Role-based authorization: `[Authorize(Roles = AppRoles.Admin)]` on administrative endpoints.
- **Controlled Initial Admin Seeder**:
  - `IAdminSeeder` / `AdminSeeder` automatically creates core roles (`Admin`, `Customer`).
  - Creates the initial administrator account on startup only when credentials are provided via `AdminSeed:Email` and `AdminSeed:Password` in `dotnet user-secrets` or environment variables.
  - Safeguard against overwriting: Never overwrites an existing administrator.
  - Production safeguard: Disabled by default in production unless explicit opt-in (`AdminSeed:EnableInProduction=true`) is provided.
  - No public setup endpoints; credentials are never hardcoded and never logged.
- **Admin Authentication Endpoints**:
  - `POST /api/admin/auth/login` — rate-limited, verifies admin role, sets session cookie.
  - `POST /api/admin/auth/logout` — requires admin authorization and valid CSRF token, clears session.
  - `GET /api/admin/auth/me` — requires admin authorization, returns safe admin profile.
  - `GET /api/admin/auth/csrf` — issues non-cacheable JSON CSRF token and `HttpOnly` cookie for frontend clients.
- **Database Migration**:
  - `20261011211920_AddAdminIdentity` created and applied to live PostgreSQL database.
- **Automated Tests**:
  - 26 total tests passing (3 Health + 10 Catalog + 13 Admin Auth & Security).

### Milestone 2B-2A — Secure Admin Catalog Management
- **Admin Category Management Endpoints**:
  - `GET /api/admin/categories` — list all categories (active & inactive) with product counts and optional `isActive` filtering.
  - `GET /api/admin/categories/{id}` — retrieve category details by ID.
  - `POST /api/admin/categories` — create new category with unique slug validation (returns 201 Created / 409 Conflict).
  - `PUT /api/admin/categories/{id}` — edit category name, description, and slug with collision checks.
  - `PATCH /api/admin/categories/{id}/status` — soft enable or disable category (disabling category hides its products from public APIs).
- **Admin Product Management Endpoints**:
  - `GET /api/admin/products` — paginated search and filter across active and inactive products (by category ID, category slug, keyword, and active status).
  - `GET /api/admin/products/{id}` — retrieve complete product details with category metadata.
  - `POST /api/admin/products` — create AI Tools product with category validation, unique SKU validation, non-negative price, and default PKR currency.
  - `PUT /api/admin/products/{id}` — edit product details, category, and pricing while strictly enforcing SKU immutability.
  - `PATCH /api/admin/products/{id}/status` — soft enable or disable product.
- **Architectural & Security Rules Enforced**:
  - All admin endpoints require `[Authorize(Roles = AppRoles.Admin)]` (anonymous returns 401, non-admin returns 401/403).
  - Global `AutoValidateAntiforgeryTokenAttribute` protects all state-changing admin requests (`POST`, `PUT`, `PATCH`) requiring CSRF tokens.
  - Safe DTOs ensure vendor mappings and supplier details are never leaked.
  - Non-destructive lifecycle: permanent deletion is forbidden for both categories and products.
  - Selling prices must not be negative (`decimal(18,4)` precision).
  - Internal SKU remains immutable after creation.
- **Automated Tests**:
  - 41 total tests passing (3 Health + 10 Catalog Public + 13 Admin Auth + 15 Admin Catalog Management).

### Milestone 2B-2B — Secure Vendor Management & Mapping
- **Admin Vendor Management Endpoints**:
  - `GET /api/admin/vendors` — search and paginate vendors; filter by active status (`?search=...`, `?isActive=...`, `?page=...`).
  - `GET /api/admin/vendors/{id}` — retrieve vendor details and product mapping counts.
  - `POST /api/admin/vendors` — create new vendor with unique internal name validation.
  - `PUT /api/admin/vendors/{id}` — edit vendor internal name and status with collision checks.
  - `PATCH /api/admin/vendors/{id}/status` — soft enable/disable vendor; preserves existing product mappings.
- **Admin Vendor Product Mapping Endpoints**:
  - `GET /api/admin/vendor-products` — filter and paginate mappings by vendor, product, SKU, availability, and currency.
  - `GET /api/admin/vendor-products/{id}` — retrieve single mapping with vendor and product details.
  - `POST /api/admin/vendor-products` — map vendor to product with referential validation, duplicate key prevention `(VendorId, ProductId)`, non-negative cost, and currency validation (PKR, USD, USDT).
  - `PUT /api/admin/vendor-products/{id}` — edit external product ID, cost, currency, availability, and routing priority.
  - `PATCH /api/admin/vendor-products/{id}/availability` — toggle mapping availability.
- **Architectural & Security Rules Enforced**:
  - All vendor endpoints require `[Authorize(Roles = AppRoles.Admin)]` and global Antiforgery / CSRF tokens.
  - Supplier isolation: vendor names, costs, currencies, and external product IDs are strictly excluded from public customer APIs.
  - Multi-currency readiness: costs stored with `decimal(18,4)` precision; distinct currencies (PKR, USD, USDT) preserved without unsafe cross-currency arithmetic.
  - Database constraint enforcement: unique composite key `(VendorId, ProductId)` and foreign keys verified against both in-memory and PostgreSQL providers.
  - Non-destructive lifecycle: permanent deletion is disabled.
- **Automated Tests**:
  - 56 total tests passing (3 Health + 10 Catalog Public + 13 Admin Auth + 15 Admin Catalog + 15 Admin Vendor & PostgreSQL constraint test).

### Milestone 3A — Central Order System Foundation
- **Domain Models & Enums**:
  - `Order` (`Id`, `OrderNumber`, `CustomerId`, `Status`, `Currency`, `Subtotal`, `TotalAmount`, `SalesChannel`, `CustomerNotes`, `CreatedAt`, `UpdatedAt`).
  - `OrderItem` (`Id`, `OrderId`, `ProductId`, `ProductSkuSnapshot`, `ProductNameSnapshot`, `Quantity`, `UnitPriceSnapshot`, `Currency`, `TotalPrice`).
  - `OrderStatusHistory` (`Id`, `OrderId`, `PreviousStatus`, `NewStatus`, `Reason`, `ChangedByUserId`, `ChangedAt`).
  - `OrderStatus` (11 strongly typed states: `PendingPayment`, `Paid`, `Processing`, `SentToVendor`, `VendorProcessing`, `Completed`, `PartiallyCompleted`, `Failed`, `Cancelled`, `RefundPending`, `Refunded`).
  - `SalesChannel` (`Website`, `Telegram`, `Smm`, `Admin`).
- **Architectural & Financial Rules Enforced**:
  - **Shared Central Architecture**: All sales channels will share a single order database and numbering system.
  - **Immutable Customer Price Snapshots**: Line items capture product SKU, product name, and unit selling price at checkout time. Subsequent updates to product price or name in the catalog never alter historical orders.
  - **Controlled State Transitions**: `OrderStatusTransitions` state machine enforces valid lifecycle flows and guards terminal states (`Cancelled`, `Refunded`).
  - **Mandatory Audit Logging**: `OrderStatusHistory` records every status transition with previous status, new status, timestamps, and initiator.
  - **Standardized Unique Order Numbers**: `IOrderNumberGenerator` produces collision-resistant order numbers (e.g. `ORD-20261013-ABC123`).
  - **Centralized Customer Identity**: Orders link directly to `ApplicationUser` (`CustomerId`), ensuring customers and administrators share one central identity foundation.
  - **Relational Integrity & Non-Destructive Referentials**: Unique index on `OrderNumber`, foreign keys with `DeleteBehavior.Restrict` on `Order -> OrderItems`, `Order -> OrderStatusHistory`, `Order -> Customer`, and `OrderItem -> Product`. Destructive cascade deletes are prohibited so orders and their audit trails cannot be accidentally wiped out. Decimal money precision `decimal(18,4)` enforced across all financial amounts.
- **Database Migrations**:
  - `20261012205237_AddCentralOrderSystem` created and applied to live PostgreSQL database.
  - `20261012210921_RestrictOrderCascadeDelete` created and applied to live PostgreSQL database (replaces CASCADE with RESTRICT on `OrderItems` and `OrderStatusHistory`).
- **Automated Tests**:
  - 75 total tests passing (3 Health + 10 Catalog Public + 13 Admin Auth + 15 Admin Catalog + 15 Admin Vendor + 19 Order System & PostgreSQL constraint tests).

### Milestone 3B — Customer Registration & Authentication
- **Central Identity Architecture**:
  - Unified single identity store across all future sales channels (tools.gogaabyte.com, smm.gogaabyte.com, Telegram bot) sharing `ApplicationUser` and `ApplicationRole`.
  - Customer profile foundation: `Id`, `FullName`, `Email`, `IsActive`, `EmailConfirmed`, `CreatedAt`, and `Roles`.
- **Strict Role & Session Segregation**:
  - Customer self-registration strictly assigns `AppRoles.Customer` server-side. Clients cannot supply role claims; privilege escalation to `Admin` is prohibited.
  - Dedicated authentication scheme `AuthSchemes.CustomerScheme` with HTTP-only cookie `.WebAPI.Customer.Auth`.
  - Customers cannot access any `/api/admin/*` endpoints (returns 401/403).
  - Admin and Customer sessions run on independent cookie schemes and cannot be confused or cross-pollinated.
- **Security & Account Protection**:
  - Normalized email indexing and duplicate registration rejection (409 Conflict).
  - ASP.NET Core Identity password policy enforcement (minimum 8 chars, uppercase, lowercase, digit, non-alphanumeric).
  - Account lockout protection (5 failed attempts -> 15-minute lockout).
  - Fixed-window rate limiting on registration and login endpoints via `CustomerAuthLimiter`.
  - Instantaneous session revocation on deactivation: `OnValidatePrincipal` checks `user.IsActive` and role on every request.
  - Cross-subdomain Antiforgery / CSRF protection: `GET /api/customer/auth/csrf` issues non-cacheable JSON tokens bound to the authenticated customer session for `tools.gogaabyte.com` and `smm.gogaabyte.com`.
  - Safe DTO serialization: Password hashes, security stamps, and backend internals are strictly excluded from all customer responses.
- **Customer API Endpoints**:
  - `POST /api/customer/auth/register` (201 Created on success, 409 Conflict on duplicate, 400 Bad Request on validation errors)
  - `POST /api/customer/auth/login` (200 OK with `.WebAPI.Customer.Auth` cookie, 401 on bad credentials, 403 on disabled, 423 on lockout)
  - `POST /api/customer/auth/logout` (200 OK; terminates cookie session; enforces CSRF)
  - `GET /api/customer/auth/me` (200 OK; returns safe `CustomerUserDto`)
  - `GET /api/customer/auth/csrf` (200 OK; returns request token for browser clients)
- **Database Status**:
  - Reused existing Identity schema; 0 migrations needed. All existing data 100% intact.
- **Automated Tests**:
  - 95 total tests passing (3 Health + 10 Catalog Public + 13 Admin Auth + 15 Admin Catalog + 15 Admin Vendor + 19 Order System + 20 Customer Auth & PostgreSQL integration tests).

### Milestone 3C — Customer Order Placement & Tracking
- **Centralized Customer Order Creation Flow**:
  - Authenticated customer order placement for AI Tools products via `POST /api/customer/orders`.
  - Orders start deterministically in `PendingPayment` status (`PaymentStatus.Unpaid`, `FulfillmentStatus.Unfulfilled`).
  - Strict server-side security:
    - Customer identity resolved exclusively from `ClaimsPrincipal` (`User.FindFirstValue(ClaimTypes.NameIdentifier)`). Client cannot supply or override `CustomerId`.
    - `SalesChannel` assigned server-side (`SalesChannel.Website`).
    - Pricing resolved directly from live PostgreSQL database (`Product.SellingPrice`). Client prices are never accepted.
    - Product active validation: Rejects non-existent, inactive, zero-priced, or products whose category is disabled.
    - Multi-currency guard: Strict enforcement of single-currency `PKR` for digital subscriptions in current phase.
    - Fixed quantity = 1 for AI tool subscription products.
- **Durable Idempotency & Concurrency Protection**:
  - Client supplies `X-Idempotency-Key` via HTTP header or payload (`IdempotencyKey` field, max 100 characters).
  - Stored in `Order.IdempotencyKey` protected by PostgreSQL filtered unique constraint index: `IX_Orders_CustomerId_IdempotencyKey` (`"IdempotencyKey" IS NOT NULL`).
  - Re-submitting an identical order with the same key returns the existing order safely (201 Created) without double-billing or duplicate records.
  - Re-submitting the same key with different order details (e.g. different SKU) is rejected with `409 Conflict`.
  - Database-level race-condition defense: Catches PostgreSQL unique violation error code `23505` if duplicate requests arrive concurrently, safely recovering and returning the winning order.
- **Collision-Resistant Order Numbers**:
  - Order numbers formatted as `ORD-YYYYMMDD-XXXXXX` (e.g., `ORD-20261012-A1B2C3`).
  - Bounded 3x collision retry loop inside relational database transaction guarantees uniqueness even during high concurrent traffic.
- **Customer Order Listing & Order Tracking**:
  - `GET /api/customer/orders`: Paginated list of customer orders with optional status filter (`Page`, `PageSize`, `Status`).
  - `GET /api/customer/orders/{orderNumber}`: Complete order tracking detail including order summary, line item snapshot, and public status history.
- **Strict Customer Isolation & Anti-Enumeration**:
  - Customers can only see and access their own orders.
  - Accessing an order number belonging to another customer returns `404 Not Found` (never `403 Forbidden`), completely preventing order enumeration and data leakage.
- **Complete Supplier Privacy**:
  - Customer order DTOs (`CustomerOrderSummaryDto`, `CustomerOrderDetailDto`, `CustomerOrderItemDto`) strictly exclude `VendorId`, `VendorProductId`, `SupplierCost`, and vendor notes.
- **Database Migration**:
  - Migration `20261012220129_AddOrderPlacementAndIdempotency` applied to PostgreSQL. Added `IdempotencyKey` column to `Orders` and created filtered unique index `IX_Orders_CustomerId_IdempotencyKey`.
- **Automated Tests**:
  - 112 total tests passing (3 Health + 10 Catalog Public + 13 Admin Auth + 15 Admin Catalog + 15 Admin Vendor + 19 Order System + 20 Customer Auth + 17 Customer Order, Concurrency & PostgreSQL constraint tests).

### Milestone 4A — Central Payment System Foundation
- **Unified Payment Data Model (`Payments`)**:
  - Central `Payment` entity linked to `Orders` (`OrderId` foreign key, `DeleteBehavior.Restrict`).
  - Collision-resistant, standardized payment identifier: `PaymentNumber` (`PAY-yyyyMMdd-XXXXXX`, unique index `IX_Payments_PaymentNumber`).
  - Monetary values stored with exact precision `decimal(18,4)` and explicit currency code (`Currency`, default `PKR`).
  - Standardized payment methods decoupled via `PaymentMethodCode` (`PaymentMethodCodes` constants: `JAZZCASH`, `EASYPAISA`, `NAYAPAY`, `SADAPAY`, `BANK_TRANSFER`, `WALLET`, `CRYPTO_BINANCE`, `CRYPTO_USDT`).
  - Confidential external provider reference: `ProviderTransactionId` (nullable, filtered index). Supplying a transaction reference never marks an order or payment as `Paid`.
  - Durable idempotency support: `IdempotencyKey` protected by PostgreSQL filtered unique index `IX_Payments_OrderId_IdempotencyKey`.
  - Timestamps: `CreatedAt`, `UpdatedAt`, `PaidAt` (set exclusively upon trusted verification), and `ExpiredAt`.
- **Payment Status Lifecycle (`PaymentStatus` & `PaymentStatusTransitions`)**:
  - Strongly typed statuses: `Pending`, `Paid`, `Failed`, `Expired`, `Refunded`, `PartiallyRefunded`.
  - Controlled status transitions: Enforces that `Failed`, `Expired`, and `Refunded` are terminal states; prohibits resurrection from failed/expired to `Paid`; requires verified administrative or cryptographic event for `Paid`.
- **Permanent Payment Audit History (`PaymentStatusHistory`)**:
  - Permanent audit trail tracking `PreviousStatus`, `NewStatus`, `Reason`, `ChangedByUserId`, and `ChangedAt`.
  - Prohibits destructive cascading deletes (`DeleteBehavior.Restrict` on `Payment -> StatusHistory`).
  - Attribution preserved with `DeleteBehavior.SetNull` on `ChangedByUserId -> AspNetUsers`.
- **Order & Payment Architectural Integrity**:
  - One-to-many relationship: One order can have multiple payment attempts (`Order.Payments`).
  - Failed or expired payment attempts do not delete or invalidate the order.
  - Decoupled lifecycle: Creating a `Pending` payment attempt does not mutate `OrderStatus.PendingPayment`.
  - Deleting an order with payment records is strictly prohibited (`DeleteBehavior.Restrict`).
- **Database Migration**:
  - Migration `20261013083725_AddCentralPaymentSystemFoundation` created with idempotent SQL script.
- **Automated Tests**:
  - 130 total tests passing (112 existing + 18 Payment Foundation tests, 0 failed, 0 skipped).

---

## Test Results

| Check | Result |
|-------|--------|
| `dotnet build` | ✅ 0 errors, 0 warnings |
| `dotnet test` (130 tests) | ✅ All passed (3 Health + 10 Catalog + 13 Admin Auth + 15 Admin Catalog + 15 Admin Vendor + 19 Order System + 20 Customer Auth + 17 Customer Order + 18 Payment Foundation) |
| Docker PostgreSQL | ✅ Migration `AddCentralPaymentSystemFoundation` applied to `webapitgbot_dev` and `webapitgbot_test`; `PostgreSqlTestDatabase` ensures auto-migration before tests |
| Database migrations applied | ✅ Up to date (`AddCentralPaymentSystemFoundation` applied across development and test databases) |
| `GET /api/health` | ✅ 200 OK — `Healthy` |
| `GET /api/categories` | ✅ 200 OK (public active only) |
| `GET /api/products` | ✅ 200 OK (public active only, vendor details strictly omitted) |
| `GET /api/admin/categories` | ✅ 200 OK (admin authenticated) / 401 Unauthorized (anon & customer) |
| `POST /api/admin/categories` | ✅ 201 Created (valid) / 409 Conflict (dup slug) / 400 (no CSRF) |
| `GET /api/admin/products` | ✅ 200 OK (paged, active + inactive) / 401 Unauthorized |
| `POST /api/admin/products` | ✅ 201 Created / 409 Conflict (dup SKU) / 400 (invalid price or category) |
| `GET /api/admin/vendors` | ✅ 200 OK (paged, search + filter) / 401 Unauthorized |
| `POST /api/admin/vendors` | ✅ 201 Created / 409 Conflict (dup name) / 400 (no CSRF) |
| `GET /api/admin/vendor-products` | ✅ 200 OK (multi-criteria filtering & pagination) |
| `POST /api/admin/vendor-products` | ✅ 201 Created / 409 Conflict (dup mapping) / 400 (invalid vendor/product/cost) |
| Customer registration | ✅ 201 Created / 409 Conflict (duplicate email) / 400 (weak password) |
| Customer role assignment | ✅ Strictly server-side `Customer` role; no Admin elevation |
| Customer login / logout | ✅ 200 OK (issues `.WebAPI.Customer.Auth`) / 401 (bad creds) / 403 (deactivated) |
| Customer /me profile | ✅ 200 OK with safe `CustomerUserDto`; zero credential leakage |
| Role & session separation | ✅ Customers strictly rejected from `/api/admin/*` endpoints (401/403) |
| Customer order creation | ✅ 201 Created with server-side pricing & initial `PendingPayment` state |
| Server-side pricing enforcement | ✅ Pricing resolved directly from DB; client prices never accepted |
| Catalog state guards | ✅ Inactive products & disabled categories rejected (400 Bad Request) |
| Durable order idempotency | ✅ Identical key returns existing order; mismatched key returns 409 Conflict |
| Customer order isolation | ✅ Other customers' orders return 404 Not Found; zero cross-customer data leakage |
| Price snapshot immutability | ✅ Past order line item price snapshots remain unchanged after catalog price updates |
| Payment entity relationships | ✅ Verified Order -> Payments and Payment -> PaymentStatusHistory |
| Unique PaymentNumber | ✅ Enforced via database unique index `IX_Payments_PaymentNumber` |
| Multiple payment attempts | ✅ Multiple failed/expired/pending attempts associated with one order without deleting order |
| Payment amounts & precision | ✅ Explicit `decimal(18,4)` precision and currency codes; non-positive amounts rejected |
| Payment status transitions | ✅ State machine enforces valid transitions and prohibits jumping to `Paid` from terminal states |
| Payment audit history | ✅ Permanent audit logs track previous status, new status, timestamp, and actor |
| Restrict delete on payments | ✅ Prohibits deleting orders with payment records or payments with history |
| Untrusted reference safety | ✅ Supplying `ProviderTransactionId` never marks a payment as `Paid` |
| Order status decoupling | ✅ Creating `Pending` payment attempt leaves `OrderStatus.PendingPayment` unchanged |
| Rate limiting & CSRF | ✅ Enforced via `CustomerOrderLimiter` and `AutoValidateAntiforgeryTokenAttribute` |
| Supplier privacy | ✅ 100% hidden from customer order endpoints and responses |
| `/swagger` UI | ✅ Available in Development |

---

## Next Milestone

- **Milestone 4B**: Customer Payment Proof Submission & Manual Admin Verification (proof receipt upload, transaction ID submission, administrator review flow, transition to `Paid` and order progression).




---

## Development Rules

- Work one module at a time.
- Never put passwords or secrets in source code.
- Never push to GitHub without owner permission.
- Run `dotnet build` and `dotnet test` after every change.
