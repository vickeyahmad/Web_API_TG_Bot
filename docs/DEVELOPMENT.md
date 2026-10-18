# Development Progress

## Current Phase: Milestone 4C — Manual PKR Payment Verification & Wallet Top-Ups ✅ COMPLETE

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

### Milestone 4B — Customer PKR Wallet & Permanent Transaction Ledger
- **Central Customer Wallet Model (`Wallets`)**:
  - Central `Wallet` entity linked directly to `ApplicationUser` (`CustomerId`, `DeleteBehavior.Restrict`).
  - Exactly one wallet per customer per currency enforced via PostgreSQL unique index `IX_Wallets_CustomerId_Currency`.
  - Non-negative balance constraint enforced at both the application level and the PostgreSQL engine level (`CK_Wallets_Balance_NonNegative`: `"Balance" >= 0`).
  - Initial balance defaults to 0.0000m with exact `decimal(18,4)` precision.
  - Client-driven balance modifications strictly prohibited; all balance changes mediated exclusively via internal `IWalletService`.
- **Permanent Transaction Ledger (`WalletTransactions`)**:
  - Immutable, append-only financial ledger recording `BalanceBefore`, `BalanceAfter`, `Amount` (> 0), `TransactionType` (Credit / Debit), `OperationKey`, `SourceType`, `SourceReferenceId`, `Description`, and timestamps.
  - Enforces positive amount constraint (`CK_WalletTransactions_Amount_Positive`: `"Amount" > 0`).
  - Traceable source categorization via `WalletTransactionSource` (`DepositVerification`, `OrderPayment`, `Refund`, `AdminAdjustment`, `CompensatingCorrection`).
  - Historical records are never modified or purged; corrections use traceable compensating transactions.
  - Referential integrity: `DeleteBehavior.Restrict` on both `User -> Wallets` and `Wallet -> WalletTransactions` (`ON DELETE RESTRICT`), preventing accidental or cascading purging of financial records.
  - Attribution preserved with `DeleteBehavior.SetNull` on `CreatedByUserId -> AspNetUsers`.
- **Internal Wallet Service (`IWalletService` / `WalletService`)**:
  - `GetOrCreateWalletAsync`: Thread-safe, racing-creation-resistant wallet initialization with initial zero balance.
  - `GetBalanceAsync`: Read-only, unpolluted customer balance retrieval.
  - `CreditAsync`: Atomically increases wallet balance and creates a permanent ledger entry.
  - `DebitAsync`: Verifies available funds, decrements balance, and creates a ledger entry. Prohibits overdrafts.
  - `GetTransactionHistoryAsync`: Chronological ledger audit history retrieval for reconciliation.
- **Concurrency & Concurrency Safeguards**:
  - Pessimistic row-level locking via PostgreSQL `SELECT * FROM "Wallets" WHERE "Id" = @id FOR UPDATE` within an explicit database transaction (`BeginTransactionAsync`).
  - Eliminates lost updates when concurrent application requests or cluster nodes mutate the same wallet.
  - Prevents overspending when two simultaneous debits race each other (only the winner succeeds; the loser receives `InsufficientFunds`).
  - Guarantees exact final balance under high concurrent distinct credit operations.
- **Durable Idempotency**:
  - Unique operation key per wallet enforced in PostgreSQL via `IX_WalletTransactions_WalletId_OperationKey`.
  - Idempotency verified before debit balance validation: Replaying an identical operation safely returns the existing transaction and resulting balance without modifying funds; re-using a key with conflicting transaction details returns 409 Conflict.
- **Scope Isolation & Zero Real-Money Movement**:
  - Standalone wallet operations have zero impact on existing Order and Payment statuses.
  - No public or customer wallet mutation endpoints; no real gateway connections enabled in this phase.
- **Database Migration**:
  - Migration `20261013113527_AddCustomerWalletAndLedgerFoundation` created and applied to both `webapitgbot_dev` and `webapitgbot_test`.
- **Automated Tests**:
  - 148 total tests passing (130 existing + 18 Wallet Integration, Concurrency & PostgreSQL constraint tests, 0 failed, 0 skipped).

### Milestone 4C — Manual PKR Payment Verification & Wallet Top-Ups
- **Central Payment Model Extension**:
  - `PaymentPurpose` enum (`OrderPurchase = 1`, `WalletTopUp = 2`).
  - Added nullable `OrderId` and nullable `WalletId` with `DeleteBehavior.Restrict`.
  - Database check constraint `CK_Payments_Purpose_Target` enforcing mutual exclusivity:
    `("Purpose" = 'OrderPurchase' AND "OrderId" IS NOT NULL AND "WalletId" IS NULL) OR ("Purpose" = 'WalletTopUp' AND "WalletId" IS NOT NULL AND "OrderId" IS NULL)`.
  - Database check constraint `CK_Payments_Amount_Positive` enforcing `"Amount" > 0`.
  - Customer transfer proof fields: `CustomerReference`, `CustomerTransferDate`, `CustomerSenderAccount`, `CustomerNotes`, `CustomerProofSubmittedAt`.
  - Admin manual verification fields: `VerifiedByUserId` (FK to AspNetUsers with `SetNull`), `VerifiedReceivingAccount`, `AdminNotes`, `VerifiedAt`.
  - PostgreSQL unique partial index `IX_Payments_Paid_ProviderTransactionId` (`WHERE "Status" = 'Paid' AND "ProviderTransactionId" IS NOT NULL`) preventing double-crediting of the same verified bank reference.
  - PostgreSQL unique partial index `IX_Payments_WalletId_IdempotencyKey` enforcing durable top-up idempotency per wallet.
- **Safety Feature Gate**:
  - `WalletTopUpOptions` with master toggle `Enabled = false` by default for production safety.
  - Returns `403 Forbidden` when disabled.
  - Configurable min/max limits (`MinAmount = 100`, `MaxAmount = 500000`), allowed payment method codes, and bank receiving details.
- **Customer Manual Top-Up Flow**:
  - `POST /api/customer/wallet/topups`: Rate-limited (`CustomerWalletTopUpLimiter`), requires Customer role, validates amount & method, creates `Payment` in `Pending` status with initial `PaymentStatusHistory`, wallet balance untouched (0). Replaying with `X-Idempotency-Key` safely returns existing payment.
  - `POST /api/customer/wallet/topups/{paymentNumber}/proof`: Customer submits bank transfer TID / reference, date, and sender account. Updates payment proof fields, appends audit note, status remains `Pending`, wallet balance is **not** credited.
  - `GET /api/customer/wallet/topups`: Paginated list of customer top-ups, ordered by creation date descending.
  - `GET /api/customer/wallet/topups/{paymentNumber}`: Detailed tracking with strict customer ownership isolation (other customers receive `404 Not Found`).
- **Administrator Verification & Single Atomic PostgreSQL Transaction**:
  - `GET /api/admin/payments/manual`: Paginated review of manual top-ups with optional status filter (`Pending`, `Paid`, `Failed`).
  - `GET /api/admin/payments/manual/{paymentNumber}`: Complete administrative detail view including customer identity, proof submission details, verification metadata, and chronological audit history.
  - `POST /api/admin/payments/manual/{paymentNumber}/reject`: Administrator rejects pending top-up with reason. Transitions status to `Failed`, records admin ID, wallet balance is untouched.
  - `POST /api/admin/payments/manual/{paymentNumber}/approve`: Administrator confirms verified bank TID. Executes inside a single atomic PostgreSQL transaction with row-level locking (`SELECT ... FOR UPDATE`):
    1. Transitions payment to `Paid`, populates `PaidAt`, `ProviderTransactionId`, and admin verification fields.
    2. Appends `PaymentStatusHistory` audit record.
    3. Atomically credits customer PKR wallet via `IWalletService.CreditAsync` using stable operation key `WALLET_TOPUP:{PaymentId}` and source `DepositVerification`.
    4. If any step or validation fails, transaction rolls back completely.
    5. Duplicate verified TID attempt rejected with `409 Conflict`.
- **Database Migration**:
  - Migration `20261013185701_AddManualWalletTopUpsAndPaymentPurpose` created and applied to `webapitgbot_dev` and `webapitgbot_test`.
- **Automated Tests**:
  - 164 total tests passing (148 existing + 16 new Milestone 4C Integration, Concurrency & PostgreSQL constraint tests, 0 failed, 0 skipped).

### Milestone 4D — Customer Wallet Purchases & Central Order Payment Processing
- **Customer Wallet Purchase Flow (`POST /api/customer/orders/{orderNumber}/pay-with-wallet`)**:
  - Authenticated customer initiates instant order payment using available PKR wallet balance.
  - Rate-limited via `CustomerOrderLimiter` with anti-forgery validation (`AutoValidateAntiforgeryToken`).
  - Feature gated via `WalletPurchaseOptions.Enabled = false` (returns `403 Forbidden` with informative message when disabled).
  - Validates order ownership (returns `404 Not Found` if order belongs to another customer or does not exist).
  - Validates order status: Requires `OrderStatus.PendingPayment` and `PaymentStatus.Unpaid`. Already paid, cancelled, or processing orders are rejected (`409 Conflict`).
  - Validates order amount: Enforces `TotalAmount > 0` and currency `PKR`.
- **Single Atomic PostgreSQL Transaction & Invariant Concurrency Lock Ordering**:
  - Coordinates Orders, Payments, and Wallets within one atomic database transaction (`BeginTransactionAsync`).
  - Acquires pessimistic row locks in strict invariant global sequence:
    1. Order locked first: `SELECT ... FROM "Orders" WHERE "OrderNumber" = @p FOR UPDATE`.
    2. Wallet locked second inside `IWalletService.DebitAsync`: `SELECT ... FROM "Wallets" WHERE "Id" = @id FOR UPDATE`.
  - Invariant locking order completely eliminates deadlock conditions between concurrent customer actions.
- **Durable Debit Idempotency & Replay Safety**:
  - Wallet debited using stable operation key `WALLET_ORDER:{order.Id}` and source `WalletTransactionSource.OrderPayment`.
  - Replaying payment with the same idempotency key (`X-Idempotency-Key`) returns the original payment confirmation (`200 OK`) without double-debiting.
  - Reusing the same idempotency key across different orders returns `409 Conflict`.
- **Database Engine Double-Payment Guard**:
  - Added PostgreSQL partial unique index `IX_Payments_Paid_OrderId` (`WHERE "Status" = 'Paid' AND "OrderId" IS NOT NULL`), guaranteeing at the database engine level that no order can ever have more than one `Paid` payment record.
  - State transitions: Creates `Payment` record in `Paid` status (`Purpose = OrderPurchase`, `PaymentMethodCode = WALLET`), appends `PaymentStatusHistory`, transitions order to `OrderStatus.Paid` and `PaymentStatus.Paid`, and appends `OrderStatusHistory`.
  - Clean Rollback: If wallet balance is insufficient, or any step fails, the entire transaction rolls back cleanly with 0 wallet balance change, 0 payment record, and 0 order status change.
- **Read-Only Customer Wallet Endpoints**:
  - `GET /api/customer/wallet`: Returns customer's wallet balance, currency (`PKR`), and active status.
  - `GET /api/customer/wallet/transactions`: Returns paginated, chronological wallet ledger history (`Page`, `PageSize`) with running balance snapshots.
- **Database Migration**:
  - Migration `20261013203158_AddUniquePaidOrderPaymentIndex` created and applied to both `webapitgbot_dev` and `webapitgbot_test`.
- **Automated Tests**:
  - 184 total tests passing (167 existing + 17 Milestone 4D Customer Wallet Order Payment & Financial Verification tests, 0 failed, 0 skipped).

### Milestone 5A — Admin Order Management & Fulfillment Preparation
- **Centralized Administrator Order Management (`AdminOrdersController` & `IAdminOrderService`)**:
  - Central order visibility across all future sales channels (AI Tools Website, Digital Products Store, Telegram Bot, SMM Panel).
  - Protected by `AppRoles.Admin`, cookie authentication (`.WebAPI.Admin.Auth`), antiforgery validation (`AutoValidateAntiforgeryToken`), and rate limiting (`AdminOrderLimiter`).
  - Strict authorization: Anonymous requests return `401 Unauthorized`; customer accounts return `403 Forbidden`.
- **Admin Order List & Multi-Criteria Filtering (`GET /api/admin/orders`)**:
  - Server-side pagination (`Page = 1` default, `PageSize = 10` default, max 100).
  - Filter parameters: `status`, `salesChannel`, `customerQuery` (matches customer ID, email, or full name), `orderNumber`, `productSku`, and date range (`fromUtc`, `toUtc`).
  - Always ordered newest first (`CreatedAtUtc` descending).
  - Returns safe `AdminOrderSummaryDto` containing order header, customer identity, payment overview, line items, and current statuses.
- **Admin Order Details & Historical Snapshot Preservation (`GET /api/admin/orders/{orderNumber}`)**:
  - Complete operational inspection view (`AdminOrderDetailDto`).
  - Preserves immutable line item snapshots (`ProductSkuSnapshot`, `ProductNameSnapshot`, `UnitPriceSnapshot`, `TotalPrice`) captured at purchase time.
  - Displays customer notes and full chronological audit trail from `OrderStatusHistory`.
  - Distinguishes and lists all `OrderPurchase` payment attempts (displaying `Pending`, `Paid`, `Failed`, `Expired`, `ProviderTransactionId`, and payment method).
  - Filters out unrelated `WalletTopUp` payments to maintain clean order accounting.
- **Controlled Manual Processing Initiation (`POST /api/admin/orders/{orderNumber}/start-processing`)**:
  - Explicit business action to transition an order from `Paid` to `Processing`.
  - Validates customer account exists and is not deleted.
  - Validates order is strictly in `OrderStatus.Paid`. Unpaid, pending payment, cancelled, or already processing orders are rejected (`409 Conflict`).
  - Verifies existence of a trusted `Paid` `OrderPurchase` payment with matching amount and currency (`PKR`).
  - State machine enforcement via `OrderStatusTransitions.CanTransition(OrderStatus.Paid, OrderStatus.Processing)`.
  - Appends an immutable `OrderStatusHistory` record capturing acting admin ID, reason, and UTC timestamp.
  - Concurrency & double-click protection: Pessimistic row lock (`SELECT ... FROM "Orders" WHERE "OrderNumber" = @p FOR UPDATE`) inside an explicit atomic database transaction ensures racing admin clicks or retries are rejected safely without duplicate history entries.
- **Zero Schema Migrations Required**:
  - Reused existing normalized schema, foreign keys, and indexes on `Orders`, `OrderItems`, `OrderStatusHistory`, and `Payments`.
- **Automated Tests**:
  - 202 total tests passing (184 existing + 18 new Milestone 5A Admin Orders & Fulfillment Preparation tests, 0 failed, 0 skipped).

### Milestone 5B — Central Fulfillment Foundation & Vendor Order Preparation
- **Central Fulfillment Entity Model (`Fulfillment`)**:
  - Central fulfillment tracking record linked directly to `Order` (`OrderId`) and `OrderItem` (`OrderItemId`) with non-destructive referential integrity (`DeleteBehavior.Restrict`).
  - Strict line-item isolation: Unique index `IX_Fulfillments_OrderItemId` ensures at most one fulfillment record per line item, preventing duplicate fulfillment initialization.
  - Snapshot protection: Preserves original product SKU, display name, and price snapshots on `OrderItem` permanently.
  - Partial fulfillment readiness: Tracks `QuantityRequested` and `QuantityFulfilled` with database check constraints (`CK_Fulfillments_QuantityRequested_Positive`: `"QuantityRequested" > 0`, `CK_Fulfillments_QuantityFulfilled_Valid`: `"QuantityFulfilled" >= 0 AND "QuantityFulfilled" <= "QuantityRequested"`).
  - Status decoupling: `FulfillmentStatus` (`NotStarted`, `Pending`, `InProgress`, `Completed`, `PartiallyCompleted`, `Failed`, `RequiresReview`) is strictly decoupled from customer `OrderStatus` and `PaymentStatus`. Creating a fulfillment record never automatically completes an order.
  - Controlled lifecycle: `FulfillmentStatusTransitions` state machine enforces valid progressions and locks terminal states (`Completed`, `Failed`).
- **Supplier Attempt & Fallback Tracking (`FulfillmentAttempt`)**:
  - Multi-supplier support: Tracks chronological vendor attempts for the same internal product without losing previous attempt logs (`(FulfillmentId, AttemptNumber)` unique composite index).
  - Durable attempt identifier: `AttemptKey` (e.g. `ATT-{FulfillmentId}-{AttemptNumber}`) with global uniqueness constraint (`IX_FulfillmentAttempts_AttemptKey`).
  - Supplier mapping preservation: Linked to `VendorProduct` mapping (nullable for manual fulfillment) with `DeleteBehavior.Restrict`.
  - Check constraint: `CK_FulfillmentAttempts_AttemptNumber_Positive` (`"AttemptNumber" > 0`).
  - No secret storage: Strictly prohibits storing passwords, supplier API tokens, customer credentials, or raw responses.
- **Critical Unknown Vendor Outcome Safety Rule**:
  - Problem solved: If an external supplier API times out or returns HTTP 500/502/503/504, it is unknown whether the supplier accepted and billed the order. Immediately trying an alternative vendor could double-purchase the subscription.
  - Safeguard implemented: `FulfillmentAttemptStatus` distinguishes `ConfirmedFailed` from `Unknown` and `RequiresReconciliation`.
  - Fallback guard: `FulfillmentAttemptRules.CanFallbackToNextVendor(status)` returns `true` ONLY if `status == ConfirmedFailed`. For `Unknown` or `RequiresReconciliation`, fallback is strictly prohibited until manual investigation or status reconciliation confirms no order was created.
  - Service enforcement: `IFulfillmentService.CreateAttemptAsync` rejects creating a subsequent attempt when the previous attempt ended in an unconfirmed outcome (`FallbackNotAllowedDueToUnconfirmedOutcome`).
- **Internal Fulfillment Service (`IFulfillmentService` / `FulfillmentService`)**:
  - `InitializeFulfillmentForOrderItemAsync`: Validates order exists, verifies order is in `Paid` or `Processing` status, verifies trusted `Paid` payment with matching amount and currency (`PKR`), checks idempotency (returns existing record if already initialized), and initializes record in `NotStarted` status. Unpaid or mismatched orders are rejected with zero financial mutation.
  - `CreateAttemptAsync`: Validates fulfillment and vendor product mapping, enforces unknown outcome fallback rules, and registers sequential attempt.
  - `RecordAttemptOutcomeAsync`: Records outcome, updates timestamps, and advances fulfillment state to `RequiresReview` or `InProgress` where appropriate.
  - Financial isolation: Zero mutation of customer wallet balances, transaction ledgers, order totals, or payment statuses.
- **Database Migration**:
  - Migration `20261014064342_AddCentralFulfillmentFoundation` created and applied to `webapitgbot_dev` and `webapitgbot_test`.
- **Automated Tests**:
  - 223 total tests passing (202 existing + 21 new Milestone 5B Fulfillment Foundation, Relationship, Concurrency & Unloaded Navigation Property tests, 0 failed, 0 skipped).

### Milestone 5C — Controlled Manual Fulfillment & Delivery Tracking
- **Administrator Manual Fulfillment Operations**:
  - `POST /api/admin/orders/{orderNumber}/items/{orderItemId}/manual-fulfillment/start` — starts manual fulfillment for an eligible line item on a `Processing` order. Creates a tracked manual `FulfillmentAttempt` (`VendorProductId = null`, `Status = Submitted`), assigns `FulfillmentMethod.Manual`, transitions fulfillment to `InProgress`, records administrator attribution and `FulfillmentStatusHistory`.
  - `POST /api/admin/orders/{orderNumber}/items/{orderItemId}/manual-fulfillment/complete` — records delivery confirmation after external delivery. Requires explicit `ConfirmDelivered = true`, safe external delivery reference, non-sensitive delivery channel, and optional operational notes. Sets `QuantityFulfilled = QuantityRequested`, transitions attempt to `Succeeded` and fulfillment to `Completed`, records `FulfillmentStatusHistory`, and completes the parent order if all line items are fulfilled.
  - `GET /api/admin/orders/{orderNumber}/fulfillments` — retrieves safe overview of all line item fulfillments, attempts, and chronological status history for an order without exposing digital delivery secrets or raw supplier credentials.
- **Architectural & Security Rules Enforced**:
  - **Pre-Condition Boundaries**: Order must be in `Processing` status with verified matching `Paid` payment funds. Unpaid orders (`PendingPayment`) or un-processed orders (`Paid`) are strictly rejected.
  - **Duplicate Delivery Prevention**: Blocked if an attempt is actively processing (`Submitted`) or if ANY prior attempt ended in an unresolved `Unknown` or `RequiresReconciliation` status. Supplier timeouts cannot be bypassed through the manual workflow.
  - **Zero Secret Storage**: Digital delivery secrets (license keys, passwords, vouchers, tokens) are strictly excluded from database entities, audit notes, and API responses. Only safe evidence metadata (ticket IDs, delivery channel) is recorded.
  - **Multi-Item Order Completion Invariant**: Completing one line item only completes the order if ALL line items on the order are fulfilled. Multi-item orders remain `Processing` until all items are completed.
  - **Permanent Audit Trail (`FulfillmentStatusHistories`)**: Every status change is recorded with previous status, new status, timestamps, and acting administrator ID. Referential integrity enforces `DeleteBehavior.Restrict` on `Fulfillments` and `DeleteBehavior.SetNull` on `AspNetUsers`.
  - **Production Safety Feature Gate**: Master toggle `ManualFulfillmentOptions.Enabled = false` by default; mutating APIs return `403 Forbidden` until explicitly enabled by server configuration.
  - **PostgreSQL Concurrency & Invariant Lock Ordering**: Order row locked first (`SELECT ... FOR UPDATE`), ensuring simultaneous completions or starts resolve with exactly one winner and zero duplicate transitions.
  - **Customer Privacy**: Customer order APIs strictly exclude fulfillment attempt details, delivery references, and vendor data.
- **Database Migration**:
  - Migration `20261014074601_AddManualFulfillmentAndAuditHistory` created and applied to `webapitgbot_dev` and `webapitgbot_test`.
- **Automated Tests**:
  - 245 total tests passing (223 existing + 22 new Milestone 5C Manual Fulfillment, Delivery Tracking & Multi-Item Order Completion tests, 0 failed, 0 skipped).

### Milestone 6A — Frontend Integration Readiness & API Contracts
- **CORS Configuration & Browser Preflight Handling**:
  - Restrictive cross-origin policy configured in `Program.cs`, `appsettings.json`, and `appsettings.Development.json`.
  - Approved production origins: `https://tools.gogaabyte.com`, `https://admin.gogaabyte.com`, `https://smm.gogaabyte.com`, and `https://api.gogaabyte.com`.
  - Approved development origins: `http://localhost:5173`, `https://localhost:5173`, `http://localhost:3000`, `https://localhost:3000`, `http://localhost:3001`, `https://localhost:3001`.
  - Permitted request headers: `Content-Type`, `X-XSRF-TOKEN`, `X-Idempotency-Key`, `Authorization`, `Accept`, `X-Requested-With`.
  - Preflight `OPTIONS` requests succeed for cross-origin customer order creation, wallet top-up, and wallet order payments without CORS header rejection.
  - Arbitrary origins (e.g. `https://malicious-website.com`) do not receive `Access-Control-Allow-Origin` headers, causing the browser to block access.
- **Dual Cookie Schemes & Session Isolation**:
  - Customer sessions (`.WebAPI.Customer.Auth`) and Admin sessions (`.WebAPI.Admin.Auth`) run on independent schemes with `HttpOnly = true`, `SameSite = Lax`, and sliding expiration.
  - In production, `CookieSecurePolicy.Always` guarantees cookies are transmitted exclusively over HTTPS.
  - Customer cookies cannot authorize admin endpoints; admin cookies cannot authorize customer endpoints.
  - Security stamp validation and immediate active status checks (`OnValidatePrincipal`) invalidate deactivated accounts on the next request.
- **Structured JSON 401 & 403 Response Bodies**:
  - Overrode `OnRedirectToLogin` and `OnRedirectToAccessDenied` on both cookie schemes to output structured `ApiResponse<object>.Fail(...)` JSON payloads instead of HTTP 302 HTML redirects, ensuring frontend `fetch` handlers receive reliable error bodies.
- **Cross-Subdomain CSRF / Antiforgery Delivery**:
  - `GET /api/customer/auth/csrf` and `GET /api/admin/auth/csrf` issue request tokens in a non-cacheable JSON response body (`Cache-Control: no-store, no-cache, must-revalidate`) while setting the companion `HttpOnly` `.WebAPI.Antiforgery` cookie.
  - Mutating state-changing requests (`POST`, `PUT`, `PATCH`, `DELETE`) require `X-XSRF-TOKEN` paired with the companion cookie. Missing or invalid tokens return `400 Bad Request`.
- **OpenAPI / Swagger Documentation Enhancement**:
  - Enabled XML documentation generation (`<GenerateDocumentationFile>true</GenerateDocumentationFile>`) in `WebApiTgBot.csproj`.
  - Integrated XML documentation into `SwaggerGen` in `Program.cs`. Swagger UI runs strictly in development (`app.Environment.IsDevelopment()`).
- **Zero Supplier Exposure in Customer DTOs**:
  - Customer catalog and order endpoints (`/api/products`, `/api/products/{sku}`, `/api/customer/orders`) strictly exclude supplier names, vendor IDs, wholesale costs, and fulfillment attempts.
- **Frontend Integration Guide for Lovable**:
  - Published comprehensive documentation in `docs/FRONTEND_INTEGRATION.md` detailing backend architecture, API endpoints, safe fetch client examples, error handling conventions, and strict frontend-only boundaries.
- **Automated Tests**:
  - 266 total tests passing (245 existing + 21 new Milestone 6A CORS, preflight, cookie auth, CSRF, JSON 401/403, and privacy tests, 0 failed, 0 skipped).

### Milestone 6B-5A2 — Secure Read-Only Admin Payments & Wallet APIs
- **Admin General Payments Overview (`GET /api/admin/payments`)**:
  - Authenticated read-only payment overview supporting both `OrderPurchase` and `WalletTopUp` attempts.
  - Server-side multi-criteria filtering (`status`, `purpose`, `paymentMethod`, `search`) and pagination with deterministic sorting (`CreatedAt` descending with unique `Id` tie-breaker).
  - Safe DTO projection (`AdminPaymentSummaryDto`) resolving customer identity from orders or wallets while strictly excluding vendor costs and credentials.
- **Admin Payment Details (`GET /api/admin/payments/{paymentNumber}`)**:
  - Safe, comprehensive operational view (`AdminPaymentDetailDto`) supporting both order purchases and wallet top-ups.
  - Preserves immutable chronological status audit trail from `PaymentStatusHistory`.
  - Sensitive customer account minimizing (`***1234`) and zero secret exposure.
  - Strict route isolation: Preserves full backward compatibility with existing `/api/admin/payments/manual` routes without conflict.
- **Admin Customer Wallets Overview (`GET /api/admin/wallets`)**:
  - Pure read-only overview of customer wallets (`AdminWalletSummaryDto`) returning authoritative stored balances.
  - Strictly prohibits wallet creation, balance mutation, or recalculation on GET requests.
  - Multi-field search across customer ID, full name, and email, with currency filtering and server-side pagination.
- **Admin Wallet Transaction History (`GET /api/admin/wallets/{walletId}/transactions`)**:
  - Wallet-scoped ledger inspection (`AdminWalletTransactionDto`) returning immutable historical records with exact `decimal(18,4)` precision and `BalanceBefore`/`BalanceAfter` snapshots.
  - Filterable by `transactionType` (Credit, Debit) and `sourceType` (DepositVerification, OrderPayment, etc.).
  - Returns 404 Not Found if wallet does not exist; strictly guarantees ledger pagination never mixes transactions from different wallets.
- **Strict Authorization & Defensive Input Validation**:
  - Enforces `[Authorize(Roles = AppRoles.Admin)]`. Anonymous requests return 401 Unauthorized; customer sessions return 401/403.
  - Defensive input validation returning 400 Bad Request for negative/zero page numbers, page size > 100, invalid enums, and invalid identifiers.
- **Zero Schema Migrations**:
  - 100% reused existing PostgreSQL schema, tables, and indexes without migrations.
- **Automated Tests**:
  - 305 total tests passing (268 baseline + 37 new Milestone 6B-5A2 integration, pagination, filtering, privacy, route-resolution, and negative validation tests, 0 failed, 0 skipped).

---

## Test Results

| Check | Result |
|-------|--------|
| `dotnet build` | ✅ 0 errors, 0 warnings |
| `dotnet test` (305 tests) | ✅ All passed (3 Health + 10 Catalog + 13 Admin Auth + 15 Admin Catalog + 15 Admin Vendor + 19 Order System + 20 Customer Auth + 17 Customer Order + 18 Payment Foundation + 18 Wallet & Ledger Foundation + 19 Manual Top-Up & Verification + 17 Customer Wallet Order Payment & Targeted Financial Verification + 18 Admin Order Management & Fulfillment Preparation + 21 Central Fulfillment Foundation + 22 Controlled Manual Fulfillment & Delivery Tracking + 21 Frontend Integration Readiness + 37 Secure Read-Only Admin Payments & Wallet APIs) |

| Docker PostgreSQL | ✅ Migrations `AddCentralFulfillmentFoundation` and `AddManualFulfillmentAndAuditHistory` applied to `webapitgbot_dev` and `webapitgbot_test`; `PostgreSqlTestDatabase` ensures auto-migration before tests |
| Database migrations applied | ✅ Up to date (No pending model changes) |
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
| Role & session separation | ✅ Customers strictly rejected from `/api/admin/*` endpoints (401/403); Admins cannot access customer endpoints |
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
| Wallet zero initial balance | ✅ Wallets initialize deterministically at 0.0000 PKR; one wallet per currency |
| Wallet non-negative balance | ✅ Enforced via check constraint `CK_Wallets_Balance_NonNegative` (`Balance >= 0`) |
| Concurrent wallet creation | ✅ Safe against racing creations; exactly one wallet per customer initialized |
| Wallet atomic mutation & lock | ✅ `SELECT ... FOR UPDATE` row locking prevents lost updates and concurrent overspending |
| Permanent ledger retention | ✅ `BalanceBefore`/`BalanceAfter` recorded; cascading deletes prohibited (`Restrict`) |
| Durable wallet idempotency | ✅ Replaying identical mutation returns original transaction; conflicting reuse returns 409 |
| Feature gate safety | ✅ `POST /api/customer/wallet/topups`, `POST /api/admin/payments/manual/{pNumber}/approve`, `POST /api/customer/orders/{orderNumber}/pay-with-wallet`, and `POST /api/admin/orders/{orderNumber}/items/{orderItemId}/manual-fulfillment/*` return 403 Forbidden when disabled by default |
| Top-up initiation & limits | ✅ Validates min/max amounts, supported methods, initial Pending status, wallet untouched |
| Customer proof submission | ✅ Records customer TID & transfer date, leaves status Pending, wallet untouched |
| Admin top-up rejection | ✅ Transitions to Failed, logs reason & admin identity, wallet untouched |
| Atomic admin approval | ✅ Atomic row lock + Paid transition + single-transaction `WalletService.CreditAsync` |
| Stable operation key | ✅ Wallet credited using durable `WALLET_TOPUP:{PaymentId}`; replay safe |
| Multi-account duplicate prevention | ✅ Unique partial index `IX_Payments_Paid_Account_ProviderTransactionId` strictly blocks reusing verified bank TID within the same receiving account while safely allowing distinct accounts |
| Ambiguous reference defense | ✅ Rejects generic or placeholder transaction references ("0000", "N/A", "CASH", "TEST", len < 4) |
| Amount & currency validation | ✅ Administrator approval strictly verifies amount and currency match payment request; mismatches rejected |
| Concurrent approval safety | ✅ Tested on PostgreSQL (`webapitgbot_test`): 2 simultaneous admin approvals result in exactly 1 success, 1 conflict, 1 credit, 0 double-funding |
| Purpose check constraint | ✅ `CK_Payments_Purpose_Target` guarantees mutually exclusive targets (Order vs Wallet) |
| Positive amount constraint | ✅ `CK_Payments_Amount_Positive` rejects non-positive payment attempts |
| Customer wallet order payment | ✅ Atomic row locks (Order then Wallet) + wallet debit + Payment Paid + Order Paid |
| Invariant lock ordering | ✅ Order row locked before Wallet row; deadlocks mathematically eliminated under concurrency |
| Engine double-payment guard | ✅ PostgreSQL partial unique index `IX_Payments_Paid_OrderId` guarantees at most 1 Paid payment per order |
| Debit durable operation key | ✅ Debited via stable `WALLET_ORDER:{order.Id}`; replays return existing confirmation without double-debiting |
| Read-only wallet & ledger | ✅ `GET /api/customer/wallet` and `GET /api/customer/wallet/transactions` return accurate balances & audit records |
| Insufficient funds rollback | ✅ Clean rollback of balance, payment, and order status with zero financial mutation |
| Rate limiting & CSRF | ✅ Enforced via `CustomerOrderLimiter`, `AdminOrderLimiter`, and `AutoValidateAntiforgeryTokenAttribute` |
| Supplier privacy | ✅ 100% hidden from customer endpoints and responses |
| Admin order listing & filtering | ✅ `GET /api/admin/orders` supports pagination, multi-criteria filtering, and newest-first ordering |
| Admin order detail snapshot | ✅ `GET /api/admin/orders/{orderNumber}` returns line item price snapshots, chronological history & order payments |
| Payment isolation in order detail | ✅ `WalletTopUp` payments strictly filtered out from order payment details |
| Admin start processing transition | ✅ `POST /api/admin/orders/{orderNumber}/start-processing` transitions `Paid` -> `Processing` with audit log |
| Unpaid/mismatched order guard | ✅ Start-processing strictly rejects unpaid orders or mismatched payment amounts |
| Concurrency & race condition safety | ✅ PostgreSQL row lock (`SELECT ... FOR UPDATE`) prevents duplicate history and racing processing |
| Admin order role & rate limiting | ✅ Restricted to `AppRoles.Admin`, CSRF-enforced, and rate-limited via `AdminOrderLimiter` |
| Fulfillment entity relationships | ✅ `Fulfillment` linked to `Order` and `OrderItem` (`DeleteBehavior.Restrict`); `FulfillmentAttempt` linked to `Fulfillment` and `VendorProduct` |
| Fulfillment line-item uniqueness | ✅ PostgreSQL unique index `IX_Fulfillments_OrderItemId` prevents duplicate fulfillment records per line item |
| Fulfillment quantity check constraints | ✅ PostgreSQL check constraints enforce `QuantityRequested > 0` and `0 <= QuantityFulfilled <= QuantityRequested` |
| Fulfillment attempt key uniqueness | ✅ PostgreSQL unique index `IX_FulfillmentAttempts_AttemptKey` and `(FulfillmentId, AttemptNumber)` composite unique |
| Fulfillment status state machine | ✅ `FulfillmentStatusTransitions` enforces valid lifecycle and locks terminal states (`Completed`, `Failed`) |
| Critical unknown outcome fallback block | ✅ `FulfillmentAttemptRules.CanFallbackToNextVendor` returns false for `Unknown` and `RequiresReconciliation`; prevents double-ordering |
| Idempotent fulfillment initialization | ✅ `IFulfillmentService.InitializeFulfillmentForOrderItemAsync` returns existing record safely without duplicate records |
| Unpaid/mismatched order guard | ✅ Unpaid orders or mismatched payment amounts are strictly rejected from fulfillment initialization |
| Zero financial mutation | ✅ Fulfillment initialization and attempt recording leave customer wallets, ledgers, and payments 100% untouched |
| Non-destructive delete protection | ✅ PostgreSQL foreign key `ON DELETE RESTRICT` prevents cascading deletion of orders, items, or fulfillments |
| Customer vendor privacy | ✅ Customer DTOs and APIs strictly hide all vendor mappings, supplier costs, and external vendor order IDs |
| Manual fulfillment start API | ✅ `POST /api/admin/orders/{orderNumber}/items/{orderItemId}/manual-fulfillment/start` starts tracked manual attempt |
| Manual fulfillment complete API | ✅ `POST /api/admin/orders/{orderNumber}/items/{orderItemId}/manual-fulfillment/complete` records delivery confirmation |
| Multi-item order completion guard | ✅ Order completed only when ALL line items are fulfilled; multi-item orders remain Processing until all items are done |
| Permanent fulfillment audit trail | ✅ `FulfillmentStatusHistories` logs all status transitions with actor and timestamps |
| Manual fulfillment feature gate | ✅ Master toggle `ManualFulfillmentOptions.Enabled = false` by default; live APIs return 403 Forbidden |
| CORS origin enforcement | ✅ Approved origins receive `Access-Control-Allow-Origin`; unapproved origins blocked |
| CORS preflight OPTIONS | ✅ Preflight requests succeed with allowed headers (`Content-Type`, `X-XSRF-TOKEN`, `X-Idempotency-Key`, etc.) |
| JSON 401/403 responses | ✅ Unauthenticated / forbidden API requests return JSON `{ success: false, data: null, message: ... }` rather than HTML redirects |
| `/swagger` UI | ✅ Available in Development with XML documentation and schema models |

---

## Next Milestone

- **Milestone 6B**: Customer Website Frontend Implementation (Lovable / React / Vite).

---

## Development Rules

- Work one module at a time.
- Never put passwords or secrets in source code.
- Never push to GitHub without owner permission.
- Run `dotnet build` and `dotnet test` after every change.



