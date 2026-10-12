# Development Progress

## Current Phase: Milestone 2B-2B — Secure Vendor Management & Mapping ✅ COMPLETE

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

---

## Test Results

| Check | Result |
|-------|--------|
| `dotnet build` | ✅ 0 errors, 0 warnings |
| `dotnet test` (56 tests) | ✅ All passed (3 Health + 10 Catalog + 13 Admin Auth + 15 Admin Catalog + 15 Admin Vendor) |
| Docker PostgreSQL | ✅ Running and healthy on `localhost:5432` |
| Database migrations applied | ✅ Schema up-to-date (no pending model changes) |
| `GET /api/health` | ✅ 200 OK — `Healthy` |
| `GET /api/categories` | ✅ 200 OK (public active only) |
| `GET /api/products` | ✅ 200 OK (public active only, vendor details strictly omitted) |
| `GET /api/admin/categories` | ✅ 200 OK (admin authenticated) / 401 Unauthorized (anon) |
| `POST /api/admin/categories` | ✅ 201 Created (valid) / 409 Conflict (dup slug) / 400 (no CSRF) |
| `GET /api/admin/products` | ✅ 200 OK (paged, active + inactive) / 401 Unauthorized |
| `POST /api/admin/products` | ✅ 201 Created / 409 Conflict (dup SKU) / 400 (invalid price or category) |
| `GET /api/admin/vendors` | ✅ 200 OK (paged, search + filter) / 401 Unauthorized |
| `POST /api/admin/vendors` | ✅ 201 Created / 409 Conflict (dup name) / 400 (no CSRF) |
| `PATCH /api/admin/vendors/{id}/status` | ✅ 200 OK (toggles active, preserves mappings) |
| `GET /api/admin/vendor-products` | ✅ 200 OK (multi-criteria filtering & pagination) |
| `POST /api/admin/vendor-products` | ✅ 201 Created / 409 Conflict (dup mapping) / 400 (invalid vendor/product/cost) |
| Multi-currency handling | ✅ PKR, USD, and USDT purchase costs supported |
| PostgreSQL constraints | ✅ Verified unique composite index `IX_VendorProducts_VendorId_ProductId` (error 23505) and FKs |
| Supplier isolation | ✅ 100% hidden from public catalog responses |
| `/swagger` UI | ✅ Available in Development |

---

## Next Milestone

- **Milestone 3A**: Customer Identity & Authentication (customer registration, login, profile management, and role-based boundaries on the centralized identity store).



---

## Development Rules

- Work one module at a time.
- Never put passwords or secrets in source code.
- Never push to GitHub without owner permission.
- Run `dotnet build` and `dotnet test` after every change.
