# Gogaabyte Frontend Integration Guide & API Contracts (Milestone 6A)

This document is the official integration specification for building the **Customer Website** (`tools.gogaabyte.com`), **Administrator Panel** (`admin.gogaabyte.com`), and future **SMM Panel** (`smm.gogaabyte.com`) using Lovable or React/Vite/Next.js.

---

> [!IMPORTANT]
> ### Strict Rules for Lovable & Frontend Development
> 1. **Frontend Only**: Build only the UI/UX components and client-side page views.
> 2. **No Supabase / No Second Backend**: Do NOT create a Supabase instance, Firebase project, Prisma ORM, or Node.js/Express server. All business logic, authentication, products, orders, payments, and wallets already live in our central ASP.NET Core backend.
> 3. **No Independent Wallet/Payment Logic**: All money calculations, wallet balances, transaction ledgers, and order prices are strictly calculated and enforced on the server. Never calculate wallet deductions or balances in browser code.
> 4. **No Secrets in localStorage**: Authentication uses secure `HttpOnly` session cookies managed by the browser. Do NOT store passwords, API keys, session tokens, or JWTs in `localStorage` or `sessionStorage`.
> 5. **Supplier Privacy**: Never request, display, or invent vendor names, wholesale purchase costs, or supplier routing. All vendor information is strictly internal.
> 6. **No Phantom Capabilities**: Only connect to endpoints that exist in this document. If a feature is disabled or not yet built, display a friendly "Currently unavailable" state.

---

## 1. Central Backend Architecture

Gogaabyte uses a single, centralized ASP.NET Core 10 modular monolith with PostgreSQL.

```
       Customer Website                   Administrator Panel               Future SMM Panel
 (https://tools.gogaabyte.com)       (https://admin.gogaabyte.com)     (https://smm.gogaabyte.com)
              │                                   │                                │
              └─────────────────────────┬─────────┴────────────────────────────────┘
                                        │ (Browser Fetch / CORS + Cookies)
                                        ▼
                             Central ASP.NET Core API
                           (https://api.gogaabyte.com)
                                        │
           ┌────────────────────────────┼────────────────────────────┐
           ▼                            ▼                            ▼
   Customer Accounts &           Orders, Wallets &           Catalog, Suppliers &
    Identity Sessions                Payments                     Fulfillments
           │                            │                            │
           └────────────────────────────┼────────────────────────────┘
                                        ▼
                         Central PostgreSQL Database
```

All sales channels connect to the same central API and database. There are no separate databases per frontend.

---

## 2. API Base URLs

| Environment | Frontend Origin(s) | Central API Base URL |
|---|---|---|
| **Local Development** | `http://localhost:5173` (Vite / Lovable default)<br>`http://localhost:3000` (Next.js default) | `https://localhost:7154` or `http://localhost:5147` |
| **Production** | `https://tools.gogaabyte.com` (Customer)<br>`https://admin.gogaabyte.com` (Admin)<br>`https://smm.gogaabyte.com` (SMM) | `https://api.gogaabyte.com` |

---

## 3. Authentication & Cookie Schemes

The backend uses **separate ASP.NET Core Identity cookie schemes** for customers and administrators.

| User Type | Cookie Name | Scope | Lifetime | Sliding Expiration |
|---|---|---|---|---|
| **Administrator** | `.WebAPI.Admin.Auth` | Admin Panel (`admin.gogaabyte.com`) | 8 Hours | Yes |
| **Customer** | `.WebAPI.Customer.Auth` | Customer Website (`tools.gogaabyte.com`) | 30 Days | Yes |

### Security Safeguards:
- **HttpOnly**: JavaScript cannot read the authentication cookies (mitigating XSS theft).
- **SameSite = Lax**: Cookies are transmitted across same-site subdomains (`tools.gogaabyte.com` -> `api.gogaabyte.com`) when `credentials: 'include'` is set.
- **Secure**: In production, cookies are only transmitted over HTTPS (`SecurePolicy = Always`).
- **Immediate Invalidation**: If an administrator deactivates an account or changes permissions, the session is invalidated immediately on the very next request via security stamp validation.
- **Strict Isolation**: Customer cookies are rejected on `/api/admin/*` endpoints (returns 401 JSON). Administrator cookies are rejected on `/api/customer/*` endpoints unless logged in through the customer scheme.

---

## 4. CSRF / Antiforgery Architecture

All state-changing HTTP requests (`POST`, `PUT`, `PATCH`, `DELETE`) require **Antiforgery validation**.

1. **Step 1 (Fetch Token)**:
   The frontend calls `GET /api/customer/auth/csrf` (or `GET /api/admin/auth/csrf`).
   - The backend sets an `HttpOnly` cookie named `.WebAPI.Antiforgery`.
   - The backend returns the request token in the JSON body:
     ```json
     {
       "success": true,
       "data": "CfDJ8N...",
       "message": "Antiforgery token generated."
     }
     ```
2. **Step 2 (Store in Memory)**:
   Store this token in a JavaScript memory variable (e.g., React Context, Pinia, or an API client variable).
3. **Step 3 (Send Header)**:
   Include the header `X-XSRF-TOKEN: <token>` and `credentials: 'include'` on all mutating requests.

> [!NOTE]
> If a mutating request arrives without `X-XSRF-TOKEN` or without the `.WebAPI.Antiforgery` cookie, the backend returns **400 Bad Request**. If this happens, fetch a fresh CSRF token and retry once.

---

## 5. API Response Contracts & Conventions

### 5.1 Response Envelope Structure

All endpoints return a uniform JSON wrapper:

```json
{
  "success": true,
  "data": { ... },
  "message": "Operation completed successfully."
}
```

On failure:
```json
{
  "success": false,
  "data": null,
  "message": "Detailed, user-friendly error message."
}
```

### 5.2 Pagination Format (`PagedResult<T>`)

Endpoints supporting pagination (products, orders, ledger, top-ups) return:

```json
{
  "items": [ ... ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 42,
  "totalPages": 3,
  "hasPreviousPage": false,
  "hasNextPage": true
}
```

### 5.3 Data Types & Formats
- **Timestamps**: All dates and times are ISO 8601 UTC strings (e.g., `"2026-10-14T12:00:00Z"`). Display in customer local time.
- **Currency**: High-precision decimal numbers/strings in **PKR** (Pakistani Rupee). Never convert to JavaScript floating point for financial calculations.
- **Idempotency Keys**: Use `X-Idempotency-Key: <UUID>` header for order creation, wallet top-up, and wallet payment to prevent duplicate charges from network retries.

---

## 6. Example Safe Frontend Fetch Client

```typescript
// apiClient.ts — Lovable / React / Vite API helper
const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5147';

let csrfToken: string | null = null;

// Fetch and cache CSRF token in memory
export async function getCsrfToken(isAdmin = false): Promise<string> {
  if (csrfToken) return csrfToken;

  const endpoint = isAdmin ? '/api/admin/auth/csrf' : '/api/customer/auth/csrf';
  const response = await fetch(`${API_BASE_URL}${endpoint}`, {
    method: 'GET',
    credentials: 'include', // Sends and receives cookies
  });

  const result = await response.json();
  if (result.success && result.data) {
    csrfToken = result.data;
    return csrfToken;
  }
  throw new Error('Failed to acquire CSRF token');
}

export function clearCsrfToken() {
  csrfToken = null;
}

// Universal fetch wrapper
export async function apiFetch<T>(
  endpoint: string,
  options: RequestInit = {},
  isAdmin = false
): Promise<{ success: boolean; data: T | null; message: string }> {
  const method = (options.method || 'GET').toUpperCase();
  const headers = new Headers(options.headers || {});
  headers.set('Accept', 'application/json');

  // Attach CSRF token on mutating HTTP methods
  if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(method)) {
    headers.set('Content-Type', 'application/json');
    const token = await getCsrfToken(isAdmin);
    headers.set('X-XSRF-TOKEN', token);
  }

  const response = await fetch(`${API_BASE_URL}${endpoint}`, {
    ...options,
    method,
    headers,
    credentials: 'include', // Required for cross-subdomain HttpOnly cookies
  });

  // Handle 400 Bad Request due to expired CSRF token
  if (response.status === 400 && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(method)) {
    clearCsrfToken();
    const freshToken = await getCsrfToken(isAdmin);
    headers.set('X-XSRF-TOKEN', freshToken);

    const retryResponse = await fetch(`${API_BASE_URL}${endpoint}`, {
      ...options,
      method,
      headers,
      credentials: 'include',
    });
    return retryResponse.json();
  }

  return response.json();
}
```

---

## 7. Customer Website API Inventory

### 7.1 Authentication & Profile

| Method | Endpoint | Role | Body | Description |
|---|---|---|---|---|
| `GET` | `/api/customer/auth/csrf` | Anonymous / Customer | None | Acquires CSRF token and sets `.WebAPI.Antiforgery` cookie. |
| `POST` | `/api/customer/auth/register` | Anonymous | `{ email, password, fullName }` | Registers a customer account. Returns 201 Created. |
| `POST` | `/api/customer/auth/login` | Anonymous | `{ email, password, rememberMe }` | Establishes `.WebAPI.Customer.Auth` cookie. Rate limited (10/min). |
| `POST` | `/api/customer/auth/logout` | Customer | None | Clears customer session cookie. Requires CSRF. |
| `GET` | `/api/customer/auth/me` | Customer | None | Returns profile (`{ id, email, fullName, createdAt }`). |

### 7.2 Public Product Catalog

| Method | Endpoint | Role | Query Parameters | Description |
|---|---|---|---|---|
| `GET` | `/api/categories` | Public | None | Lists all active categories (`[{ id, name, slug, description }]`). |
| `GET` | `/api/products` | Public | `category`, `search`, `page`, `pageSize` | Lists active products in active categories (`PagedResult<ProductSummaryDto>`). |
| `GET` | `/api/products/{sku}` | Public | None | Returns details for an active product by SKU (`ProductDetailDto`). Returns 404 if inactive. |

> [!NOTE]
> Public product responses include: `internalSku`, `name`, `description`, `sellingPrice`, `currency` ("PKR"), `categorySlug`, `categoryName`. Vendor names, costs, and supplier IDs are **never** present.

### 7.3 Customer Orders

| Method | Endpoint | Role | Headers / Body | Description |
|---|---|---|---|---|
| `POST` | `/api/customer/orders` | Customer | `X-Idempotency-Key`<br>`{ items: [{ productSku, quantity }] }` | Creates an order in `PendingPayment` status. Price snapshot is frozen on the server. |
| `GET` | `/api/customer/orders` | Customer | `status`, `page`, `pageSize` | Lists authenticated customer's orders. |
| `GET` | `/api/customer/orders/{orderNumber}` | Customer | None | Returns order details, item snapshots, and tracking history. (404 if not owned by customer). |

### 7.4 Customer PKR Wallet & Payments

| Method | Endpoint | Role | Headers / Body | Description |
|---|---|---|---|---|
| `GET` | `/api/customer/wallet` | Customer | None | Returns `{ balance, currency: "PKR", updatedAt }`. Initial balance is 0.00. |
| `GET` | `/api/customer/wallet/transactions` | Customer | `page`, `pageSize` | Lists paginated wallet ledger transactions (`amount`, `transactionType`, `balanceAfter`, `createdAt`). |
| `POST` | `/api/customer/wallet/topups` | Customer | `X-Idempotency-Key`<br>`{ amount, paymentMethodCode }` | Requests a manual top-up (Creates Pending payment). **Feature Gated**. |
| `POST` | `/api/customer/wallet/topups/{paymentNumber}/proof` | Customer | `{ providerTransactionId, senderAccountInfo, transferDate }` | Submits bank TID proof. Remains Pending awaiting admin review. |
| `GET` | `/api/customer/wallet/topups` | Customer | `page`, `pageSize` | Lists top-up attempts. |
| `GET` | `/api/customer/wallet/topups/{paymentNumber}` | Customer | None | Tracks a single top-up attempt. |
| `POST` | `/api/customer/orders/{orderNumber}/pay-with-wallet` | Customer | `X-Idempotency-Key` | Pays for order from wallet balance. Atomically marks order `Paid`. **Feature Gated**. |

---

## 8. Administrator Panel API Inventory

All administrative endpoints require active `.WebAPI.Admin.Auth` session cookie and `AppRoles.Admin`.

### 8.1 Administrator Auth & Profile

| Method | Endpoint | Role | Body | Description |
|---|---|---|---|---|
| `GET` | `/api/admin/auth/csrf` | Anonymous / Admin | None | Generates admin CSRF token and sets `.WebAPI.Antiforgery` cookie. |
| `POST` | `/api/admin/auth/login` | Anonymous | `{ email, password, rememberMe }` | Authenticates administrator. Rate-limited, 5 failed attempts trigger 15-minute lockout. |
| `POST` | `/api/admin/auth/logout` | Admin | None | Clears administrator session. Requires CSRF. |
| `GET` | `/api/admin/auth/me` | Admin | None | Returns administrator profile (`AdminUserDto`). |

### 8.2 Category & Product Management

| Method | Endpoint | Role | Description |
|---|---|---|---|
| `GET` | `/api/admin/categories` | Admin | Lists all categories (query: `isActive`). |
| `GET` | `/api/admin/categories/{id}` | Admin | Retrieves category by ID. |
| `POST` | `/api/admin/categories` | Admin | Creates category (`{ name, slug, description }`). |
| `PUT` | `/api/admin/categories/{id}` | Admin | Updates category (`{ name, slug, description }`). |
| `PATCH` | `/api/admin/categories/{id}/status` | Admin | Toggles active status (`{ isActive }`). Soft toggle; deletion disallowed. |
| `GET` | `/api/admin/products` | Admin | Lists products with filters (query: `search`, `categoryId`, `isActive`, `page`, `pageSize`). |
| `GET` | `/api/admin/products/{id}` | Admin | Retrieves product by ID. |
| `POST` | `/api/admin/products` | Admin | Creates product (`{ categoryId, internalSku, name, description, sellingPrice }`). SKU is immutable. |
| `PUT` | `/api/admin/products/{id}` | Admin | Updates product name, description, category, and selling price. |
| `PATCH` | `/api/admin/products/{id}/status` | Admin | Toggles active status (`{ isActive }`). |

### 8.3 Supplier & Mapping Management (Internal Only)

| Method | Endpoint | Role | Description |
|---|---|---|---|
| `GET` | `/api/admin/vendors` | Admin | Lists suppliers (query: `search`, `isActive`, `page`, `pageSize`). |
| `GET` | `/api/admin/vendors/{id}` | Admin | Retrieves supplier details. |
| `POST` | `/api/admin/vendors` | Admin | Creates supplier (`{ internalName }`). |
| `PUT` | `/api/admin/vendors/{id}` | Admin | Updates supplier name. |
| `PATCH` | `/api/admin/vendors/{id}/status` | Admin | Toggles supplier active status. |
| `GET` | `/api/admin/vendor-products` | Admin | Lists product-to-supplier mappings. |
| `POST` | `/api/admin/vendor-products` | Admin | Maps supplier to product (`{ vendorId, productId, externalProductId, vendorCost, vendorCostCurrency, priority }`). |
| `PATCH` | `/api/admin/vendor-products/{id}/availability` | Admin | Toggles mapping availability (`{ isAvailable }`). |

### 8.4 Administrator Order & Fulfillment Operations

| Method | Endpoint | Role | Description |
|---|---|---|---|
| `GET` | `/api/admin/orders` | Admin | Search orders across all channels (query: `status`, `salesChannel`, `customerEmail`, `orderNumber`, `sku`, `page`, `pageSize`). |
| `GET` | `/api/admin/orders/{orderNumber}` | Admin | Complete order snapshot, customer info, price snapshots, and `OrderStatusHistory`. |
| `POST` | `/api/admin/orders/{orderNumber}/start-processing` | Admin | Moves order from `Paid` to `Processing`. Verifies matching payment. |
| `GET` | `/api/admin/payments/manual` | Admin | Lists manual wallet top-up payment attempts. |
| `GET` | `/api/admin/payments/manual/{paymentNumber}` | Admin | Comprehensive payment audit history and bank reference details. |
| `POST` | `/api/admin/payments/manual/{paymentNumber}/approve` | Admin | Approves payment, verifies bank statement receipt, and credits PKR wallet. **Feature Gated**. |
| `POST` | `/api/admin/payments/manual/{paymentNumber}/reject` | Admin | Rejects payment attempt. Marks payment `Failed`. Wallet is untouched. |
| `POST` | `/api/admin/orders/{orderNumber}/items/{orderItemId}/manual-fulfillment/start` | Admin | Starts manual fulfillment attempt on a `Processing` order item. **Feature Gated**. |
| `POST` | `/api/admin/orders/{orderNumber}/items/{orderItemId}/manual-fulfillment/complete` | Admin | Confirms delivery (`confirmDelivered: true`, `deliveryReference`, `deliveryChannel`). Completes order if all items done. **Feature Gated**. |
| `GET` | `/api/admin/orders/{orderNumber}/fulfillments` | Admin | Inspects all fulfillment attempts and immutable status audit trails for an order. |

---

## 9. Feature Gate Configuration & Handling

To protect financial integrity during staged deployment, dangerous operations are protected by server-side feature flags.

| Feature Flag | Configuration Key | Default in Production | Status Code When Disabled |
|---|---|---|---|
| **Manual Wallet Top-Up** | `WalletTopUp:Enabled` | `false` | `403 Forbidden` |
| **Wallet Order Purchase** | `WalletPurchase:Enabled` | `false` | `403 Forbidden` |
| **Manual Fulfillment** | `ManualFulfillment:Enabled` | `false` | `403 Forbidden` |

### Frontend UI Rule:
When an endpoint returns `403 Forbidden` with a message containing `"disabled"` or `"currently unavailable"`:
- The frontend must display an informative banner or modal:
  *"Wallet payments/top-ups are currently undergoing scheduled maintenance. Please check back soon."*
- **Never** display a generic crash screen or treat a disabled feature as a successful operation.

---

## 10. Local Development Setup

To test Lovable or local React apps against the backend:

1. **Start Backend**:
   ```powershell
   cd D:\Projects\Web_API_TG_Bot\src\WebApiTgBot
   dotnet run
   ```
   Backend listens on `http://localhost:5147` and `https://localhost:7154`.
2. **Start Frontend (Vite / Lovable)**:
   ```bash
   npm run dev
   ```
   Vite runs on `http://localhost:5173`.
3. **CORS is Pre-Configured**:
   `appsettings.Development.json` already allows:
   - `http://localhost:5173`
   - `https://localhost:5173`
   - `http://localhost:3000`
   - `https://localhost:3000`
4. **Interactive Swagger Docs**:
   Open `http://localhost:5147/swagger` in your browser to inspect the complete OpenAPI v1 schema and test endpoints interactively.

---

## 11. Production Deployment Architecture

In production:
- Customer Website: `https://tools.gogaabyte.com`
- Admin Panel: `https://admin.gogaabyte.com`
- Future SMM Panel: `https://smm.gogaabyte.com`
- Central Backend: `https://api.gogaabyte.com`

All frontends and backend share the parent domain `gogaabyte.com`. This ensures `SameSite = Lax` and `HttpOnly` cookies operate seamlessly with cross-subdomain fetch requests.

---

## 12. Genuinely Missing Features for Future Milestones

Do **NOT** implement or assume the following features exist on the backend yet:
1. **Automated Payment Gateways**: EasyPaisa/JazzCash merchant APIs, Stripe, or crypto webhooks are not implemented. Only administrator-verified manual top-ups are supported.
2. **Automatic Supplier APIs**: Vendor API automated ordering is not yet implemented. All fulfillment is manual.
3. **Telegram Bot Webhooks / SMM Panel Logic**: Scheduled for later milestones.
4. **Customer Password Reset via Email**: Registration creates accounts with `EmailConfirmed = false`. Transactional email delivery will be added in a future milestone.
5. **Refund Processing / Partial Order Cancellations**: Refunds and partial cancellations are not yet automated.
