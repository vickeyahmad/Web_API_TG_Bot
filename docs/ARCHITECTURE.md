# Architecture

Backend: ASP.NET Core 10 / C#
Database: PostgreSQL
Frontend: Next.js / React
Containers: Docker
Cache and Jobs: Redis (when needed)
Architecture: Modular Monolith

## Central Flow

Customer Website / Telegram / SMM / Admin
             |
      ASP.NET Core API
             |
   Central Business Logic
             |
  PostgreSQL + Vendor Adapters

Frontend channels must not directly
connect to vendor APIs or the database.

Begin with manual vendor fulfillment.
Add automated vendor adapters later.

Use secure authentication, validation,
audit logs and server-side payment checks.
