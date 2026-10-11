# Development Progress

## Current Phase: Milestone 2A — Central Product Catalog and Vendor Mapping ✅ COMPLETE

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
- **Domain Models**:
  - `Category` (`Id`, `Name`, `Slug` [unique], `Description`, `IsActive`, `CreatedAt`, `UpdatedAt`)
  - `Product` (`Id`, `InternalSku` [unique], `Name`, `Description`, `CategoryId`, `SellingPrice`, `SellingCurrency`, `IsActive`, `CreatedAt`, `UpdatedAt`)
  - `Vendor` (`Id`, `InternalName`, `IsActive`, `CreatedAt`, `UpdatedAt`)
  - `VendorProduct` (`Id`, `VendorId`, `ProductId`, `ExternalProductId`, `VendorCost`, `VendorCostCurrency`, `IsAvailable`, `Priority`, `CreatedAt`, `UpdatedAt`)
- **Entity Framework Core Configurations**:
  - Explicit decimal precision `decimal(18,4)` for money fields (`SellingPrice`, `VendorCost`).
  - Unique index on `Category.Slug` and `Product.InternalSku`.
  - Unique composite index on `VendorProduct(VendorId, ProductId)` preventing duplicate mappings.
  - Performance indexes on `Products(CategoryId, IsActive)` and `VendorProducts(ProductId, IsAvailable, Priority)`.
  - Restrict delete on Category-to-Product; Cascade delete on VendorProduct mappings.
- **Multi-Currency Readiness**:
  - Selling currency (`SellingCurrency`, default `PKR`) tracked separately from vendor cost currency (`VendorCostCurrency` e.g., `PKR`, `USD`, `USDT`).
  - USDT kept distinct from USD.
- **Service Layer**:
  - `ICatalogService` and `CatalogService` implementing business logic, filtering, search, pagination, and SKU lookups.
  - Inactive categories and inactive products are strictly excluded from public queries.
- **Public Read-Only DTOs**:
  - `CategoryDto`, `ProductSummaryDto`, `ProductDetailDto`, `PagedResult<T>`.
  - Database entities are never serialized directly; vendor information is strictly absent from all DTOs.
- **Public API Endpoints**:
  - `GET /api/categories` — returns active categories.
  - `GET /api/products` — returns active products in active categories with category filter, search, and pagination.
  - `GET /api/products/{sku}` — returns product details by internal SKU; returns 404 for missing/inactive items.
- **Database Migrations**:
  - `20261011205814_AddCatalogAndVendorMapping` created and applied to live PostgreSQL database in Docker.
- **Automated Tests**:
  - 13 total tests passing (3 Milestone 1 health tests + 10 Milestone 2A catalog tests).
  - Explicit security assertion tests confirming vendor names, vendor costs, and vendor mapping keys never leak into public responses.

---

## Test Results

| Check | Result |
|-------|--------|
| `dotnet build` | ✅ 0 errors, 0 warnings |
| `dotnet test` (13 tests) | ✅ All passed (3 Health + 10 Catalog) |
| Docker PostgreSQL | ✅ Running and healthy on `localhost:5432` |
| Database migration applied | ✅ `AddCatalogAndVendorMapping` applied to PostgreSQL |
| `GET /api/health` | ✅ 200 OK — `Healthy` |
| `GET /api/categories` | ✅ 200 OK |
| `GET /api/products` | ✅ 200 OK |
| `/swagger` UI | ✅ Available in Development |

---

## Next Milestone

- **Milestone 2B**: Admin Catalog Management & Authentication (secure internal endpoints for managing categories, products, vendors, and vendor mappings).

---

## Development Rules

- Work one module at a time.
- Never put passwords or secrets in source code.
- Never push to GitHub without owner permission.
- Run `dotnet build` and `dotnet test` after every change.
