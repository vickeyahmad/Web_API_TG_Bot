# Architecture Decisions

1. ASP.NET Core 10 is the main backend.
2. PostgreSQL is the central database.
3. Next.js will provide the customer website.
4. Start with a modular monolith.
5. All sales channels share the same backend.
6. Vendor integrations use separate adapters.
7. Start with manual payment and fulfillment.
8. Never expose vendor costs or credentials.
9. GitHub is the source of truth for project code.
10. Products have their own internal SKU (`InternalSku`) decoupled from external vendor product IDs.
11. Vendor mappings (`VendorProduct`) allow multiple suppliers per internal product with individual costs and priority rankings.
12. Multi-currency readiness: Customer selling currency (`PKR` default) and supplier costs (`PKR`, `USD`, `USDT`) are stored separately with explicit `decimal(18,4)` precision. No direct subtraction across differing currencies.
13. Public API outputs use strict DTOs (`CategoryDto`, `ProductSummaryDto`, `ProductDetailDto`) to ensure vendor details and vendor costs are never serialized, avoiding data leakage.
14. Future customer wallet ledger and payment provider requirements are planned without schema conflicts.
15. Central identity architecture: Single ASP.NET Core Identity store on `AppDbContext` using `ApplicationUser` and `ApplicationRole`. Serves administrators now and will support customers in future milestones without creating separate authentication backends.
16. Secure session management: HTTP-only authentication cookies (`.WebAPI.Admin.Auth`) configured with `SameSite = Lax` and sliding expiration. `SameSite = Lax` enables cross-subdomain requests (`admin.gogaabyte.com` to `api.gogaabyte.com`) with credentials while preventing cross-site attacks. Web API overrides default redirect behaviors to return 401 Unauthorized and 403 Forbidden.
17. Defense-in-depth against credential attacks: Built-in fixed window rate limiting on `/api/admin/auth/login` coupled with Identity account lockout (15-minute lockout after 5 consecutive failed attempts).
18. Controlled administrator provisioning: Initial administrator account is seeded on startup only if none exists and credentials are supplied through secure environment variables (`ADMIN_SEED_EMAIL`, `ADMIN_SEED_PASSWORD`) or `dotnet user-secrets`. No public registration or setup endpoints exist.
19. Preserved Security Stamp Validation & Instant Role/Active Checks: `OnValidatePrincipal` explicitly invokes `SecurityStampValidator.ValidatePrincipalAsync(context)` alongside checks for `user.IsActive` and admin role membership. With `SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero`, password resets, demotions, and account deactivations revoke active sessions immediately on the next request.
20. Cross-subdomain CSRF token delivery: `GET /api/admin/auth/csrf` returns the CSRF request token in a non-cacheable JSON response (`Cache-Control: no-store, no-cache, must-revalidate`) while keeping the companion `.WebAPI.Antiforgery` cookie `HttpOnly = true`. This avoids assuming frontend JS can read host-only backend cookies across subdomains (`admin.gogaabyte.com` vs `api.gogaabyte.com`).
21. Centralized CSRF enforcement: Globally registered `AutoValidateAntiforgeryTokenAttribute` via `AddControllersWithViews` in `Program.cs`. All state-changing endpoints (`POST`, `PUT`, `PATCH`, `DELETE`) across all current and future controllers automatically require valid antiforgery tokens unless explicitly exempted with `[IgnoreAntiforgeryToken]`.
22. Admin Catalog Management & SKU Immutability: Dedicated `AdminCategoriesController` and `AdminProductsController` powered by `IAdminCatalogService`. To ensure referential stability and protect downstream orders/vendor mappings, product `InternalSku` is strictly immutable after creation.
23. Non-Destructive Catalog Lifecycle: Permanent deletion of categories and products is disallowed. Inactive states are toggled via dedicated `PATCH .../status` endpoints. Disabling a category automatically hides all its products from public customer-facing APIs without deleting records.


