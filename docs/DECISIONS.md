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
