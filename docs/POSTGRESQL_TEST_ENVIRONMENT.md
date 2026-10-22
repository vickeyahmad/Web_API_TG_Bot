# PostgreSQL Test Environment & Operational Runbook

## Overview
This runbook documents the architecture, configuration, safety guards, credential rotation procedures, backup/recovery steps, and staged execution checklist for the isolated PostgreSQL integration test environment in **Gogaa Byte**.

---

## 1. Environment Architecture & Isolation Topology

| Property | Development Environment | Isolated Test Environment |
| :--- | :--- | :--- |
| **Instance Type** | Local Docker Container | Separate Local Docker Container |
| **Canonical Compose File** | `docker/docker-compose.yml` | `docker-compose.test.yml` (at repository root) |
| **Compose Project Name** | `docker` (default) | `gogaabyte-test` (explicit unique identity) |
| **Container Name** | `webapitgbot_db` | `webapitgbot_test_db` |
| **Host Port Binding** | `127.0.0.1:5432:5432` | `127.0.0.1:5433:5432` |
| **Database Name** | `webapitgbot_dev` | `webapitgbot_test` |
| **Bootstrap Superuser** | `postgres` (or initial creator) | `postgres` (used only by container `initdb` entrypoint) |
| **Test Application Role** | `webapitgbot` | `gogaa_test_runner` |
| **Role Attributes** | Development User / DB Owner | `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS` |
| **Docker Volume** | `postgres_data` | `postgres_test_data` |
| **Docker Network** | Default bridge | Dedicated `test_network` bridge |
| **Credential Sourcing** | `dotnet user-secrets` (`DefaultConnection`) | Environment variable `GOGAA_TEST_POSTGRES_CONNECTION` |
| **Execution Opt-In** | Automatic in Development | Explicit `GOGAA_ENABLE_POSTGRES_TESTS=true` required |

---

## 2. Docker Test Configuration (`docker-compose.test.yml`)

The test container is configured in `docker-compose.test.yml` with the following non-negotiable isolation rules:
- **Canonical Root Location**: `docker-compose.test.yml` at the repository root is the sole, authoritative test compose configuration.
- **Unique Project Identity**: `name: gogaabyte-test` isolates this container from the development stack.
- **Dedicated Port**: Bound strictly to `127.0.0.1:5433`. The safety guard actively refuses any connection string pointing to port `5432`.
- **Dedicated Storage**: Named volume `postgres_test_data` ensures test mutations never touch `postgres_data`.
- **Least-Privilege Role Bootstrap**: The container boots with superuser `postgres`, executing `docker/test-init/01-init-test-db.sh` during initialization to create `webapitgbot_test` and provision `gogaa_test_runner` with explicit `NOSUPERUSER` privileges.
- **Password Safety & SQL Injection Prevention**: The initialization script `01-init-test-db.sh` uses a quoted heredoc (`<<'EOSQL'`) to prevent shell parameter expansion, and uses `psql`'s built-in `\getenv` and literal interpolation (`:'test_runner_password'`). Passwords containing single quotes, dollar signs, backslashes, or semicolons cannot break SQL syntax or inject commands. Passwords are never echoed to logs or passed as command-line arguments.
- **No Embedded Passwords**: Both `GOGAA_TEST_ADMIN_PASSWORD` (bootstrap superuser) and `GOGAA_TEST_POSTGRES_PASSWORD` (test runner role) must be supplied at runtime. Docker Compose fails immediately if either variable is missing.
- **Engine-Level Readiness Healthcheck**: `pg_isready -h localhost -p 5432 -U postgres` verifies that the PostgreSQL server process is accepting connections. Note that `pg_isready` reports server socket availability only and does not perform user authentication. Dedicated test role authentication and privileges are verified separately by the C# application preflight check (`SELECT current_database(), current_user`).
- **Secret Exposure Limitations**: Environment variables passed into containers are visible on the local workstation to processes with Docker daemon access (e.g. via `docker inspect`). While standard for local development and disposable testing, test credentials must never be committed to Git or pushed to remote repositories.
- **Non-Destructive Default**: Standard shutdown command `docker compose -f docker-compose.test.yml down` stops containers without deleting volumes.

---

## 3. Test Safety Guards

The C# test infrastructure in `tests/WebApiTgBot.Tests/PostgreSqlTestSafetyGuard.cs` enforces:
1. **Explicit Opt-in**: `GOGAA_ENABLE_POSTGRES_TESTS=true` must be set in the process environment.
2. **Dedicated Connection String**: `GOGAA_TEST_POSTGRES_CONNECTION` must be provided; never falls back to development settings.
3. **Exact Database Allowlist**: Database name must strictly equal `webapitgbot_test`. Any name containing `dev`, `prod`, `stage`, or `live` is rejected.
4. **Exact Role Allowlist**: Database username must strictly equal `gogaa_test_runner`. Connecting as `webapitgbot`, `postgres`, `root`, or `admin` is strictly rejected.
5. **Port Enforcement**: Port must equal `5433`. Connecting to development port `5432` is caught with a dedicated safety violation exception.
6. **Engine-Level Live Verification**: Before any migration or HTTP test request is processed, the guard runs `SELECT current_database(), current_user;` and verifies that the live database engine confirms both `webapitgbot_test` and `gogaa_test_runner`.
7. **Mandatory Preflight**: All HTTP requests (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS) fail closed if database preflight was not successfully completed.

---

## 4. Development Credential Rotation Procedure

> [!IMPORTANT]
> Development credential rotation (port 5432) and isolated test container provisioning (port 5433) are **two separately authorized operations**. Do not conflate them.

### Problem Statement
A historical commit (`c7cdb09`) committed a PostgreSQL password in a test helper file on GitHub. Our audit verified that this password was shared with local development `dotnet user-secrets`.

### Prerequisites Before Rotation
1. Verify the development database container `webapitgbot_db` is running on port 5432.
2. Complete a verified backup of `webapitgbot_dev` (see Section 5 below).
3. Do not rotate credentials during active development or without owner approval.

### Step-by-Step Rotation Instructions (Zero Command-Line History Leakage)

#### Step 1: Rotate Password Inside PostgreSQL Using Interactive Prompt
Avoid running `ALTER USER ... WITH PASSWORD '...'` directly in command lines where shell history or process tables could record the plaintext password. Instead, use `psql`'s built-in interactive `\password` meta-command:

```powershell
# 1. Connect to development container as postgres superuser
docker exec -it webapitgbot_db psql -U postgres -d webapitgbot_dev
```
Inside the interactive `psql` prompt:
```sql
-- The \password command prompts for the password with input masking.
-- The password is NOT recorded in .psql_history or command logging.
\password webapitgbot
-- Enter new password when prompted, then re-enter to confirm.
\q
```

#### Step 2: Update Development `dotnet user-secrets` Securely
To prevent saving the new connection string with the plaintext password in PowerShell command history (`(Get-PSReadLineOption).HistorySavePath`), use an interactive prompt in PowerShell:

```powershell
cd D:\Projects\Web_API_TG_Bot\src\WebApiTgBot

# Prompt for the new password securely (characters masked)
$newPassword = Read-Host -Prompt "Enter new development PostgreSQL password" -AsSecureString
$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($newPassword)
$plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
[System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)

# Set the connection string in local user-secrets without history exposure
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=webapitgbot_dev;Username=webapitgbot;Password=$plainPassword"

# Wipe the plain variable from memory
Clear-Variable plainPassword -Force
```

#### Step 3: Verify Application Connectivity
```powershell
# Run backend build and verify health check in development
dotnet build
```

#### Step 4: Recovery / Rollback (If Connectivity Fails)
1. If the application cannot connect, re-connect via `docker exec -it webapitgbot_db psql -U postgres -d webapitgbot_dev` and re-run `\password webapitgbot`.
2. If database access issues persist, restore from the pre-rotation backup.

---

## 5. Development Database Backup & Recovery Procedure

### Backup Procedure (Outside Active Data Volume)
> [!WARNING]
> Do NOT write backups directly into `/var/lib/postgresql/data` (which pollutes the PostgreSQL engine data directory).
> Do NOT use PowerShell redirection (`>`) with `docker exec`, as Windows PowerShell line-ending and character re-encoding can corrupt binary custom-format (`-F c`) archives.

Use container `/tmp` and `docker cp` for binary-safe extraction:

```powershell
# 1. Ensure local backup directory exists
$backupDir = "D:\Backups\GogaaByte"
if (!(Test-Path $backupDir)) { New-Item -ItemType Directory -Force -Path $backupDir }

# 2. Generate timestamped dump in container's /tmp directory
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$containerTmpDump = "/tmp/backup_dev_${timestamp}.dump"
docker exec webapitgbot_db pg_dump -U webapitgbot -d webapitgbot_dev -F c -b -f $containerTmpDump

# 3. Safely copy the binary dump from container to Windows host
$hostBackupFile = "$backupDir\webapitgbot_dev_${timestamp}.dump"
docker cp "webapitgbot_db:$containerTmpDump" $hostBackupFile

# 4. Remove temporary dump from container
docker exec webapitgbot_db rm $containerTmpDump

# 5. Verify backup file size and binary header integrity
$file = Get-Item $hostBackupFile
Write-Host "Backup Created: $($file.FullName) ($($file.Length) bytes)"
if ($file.Length -lt 1000) {
    Write-Error "Backup file is suspiciously small. Verify pg_dump execution."
} else {
    $bytes = [System.IO.File]::ReadAllBytes($hostBackupFile)
    $magic = [System.Text.Encoding]::ASCII.GetString($bytes[0..4])
    if ($magic -eq "PGDMP") {
        Write-Host "Integrity Verified: Valid PostgreSQL custom-format (PGDMP) archive detected."
    } else {
        Write-Error "Integrity Failed: Missing PGDMP header."
    }
}
```

### Restore Verification Procedure (In Separate Verification Database Only)
To test a restore safely, **NEVER** restore directly over an existing development database. Use a separate, temporary database:
```powershell
# 1. Create a dedicated temporary verification database
docker exec -it webapitgbot_db psql -U postgres -c "CREATE DATABASE dev_restore_verify;"

# 2. Copy the backup file into container /tmp
docker cp $hostBackupFile "webapitgbot_db:/tmp/restore_verify.dump"

# 3. Restore the dump into the verification database
docker exec webapitgbot_db pg_restore -U postgres -d dev_restore_verify /tmp/restore_verify.dump

# 4. Verify table counts and schema in verification database
docker exec -it webapitgbot_db psql -U postgres -d dev_restore_verify -c "\dt"

# 5. Clean up verification database and temporary dump
docker exec -it webapitgbot_db psql -U postgres -c "DROP DATABASE dev_restore_verify;"
docker exec webapitgbot_db rm /tmp/restore_verify.dump
```

---

## 6. Docker Cleanup Safety Rules

### Identity Verification Before Any Cleanup
To ensure development data is never accidentally deleted when managing test resources:

| Resource | Development (PROTECTED) | Test (DISPOSABLE) |
| :--- | :--- | :--- |
| **Compose Project** | `docker` | `gogaabyte-test` |
| **Container Name** | `webapitgbot_db` | `webapitgbot_test_db` |
| **Volume Name** | `postgres_data` | `postgres_test_data` |
| **Host Port** | `5432` | `5433` |

### Default Safe Shutdown (Non-Destructive)
When finished testing, shut down the container safely without deleting the volume:
```powershell
# Preserves postgres_test_data volume for investigation if needed
docker compose -f docker-compose.test.yml down
```

### Destructive Volume Deletion Safeguards (`down -v`)
Volume deletion must **NEVER** be treated as a routine or default operation. Destructive deletion is permitted ONLY when all of the following conditions are met:
1. Operator explicitly authorizes wiping test data.
2. The compose command explicitly targets `-f docker-compose.test.yml` (never `docker/docker-compose.yml`).
3. Project identity is confirmed as `gogaabyte-test`.
4. Target volume is confirmed as `postgres_test_data`.
```powershell
# AUTHORIZED OPERATOR USE ONLY:
docker compose -f docker-compose.test.yml down -v
```

### Strictly Prohibited Commands
❌ `docker system prune -a --volumes` (DANGEROUS: Wipes all unattached Docker volumes on the machine).
❌ `docker volume prune` without volume name verification.
❌ Running `docker compose down -v` from inside the `docker/` directory.

---

## 7. Future Integration Test Execution Checklist (Milestone 6B-6E3C2B / C)

Before executing the first PostgreSQL test:

- [ ] **1. Explicit Owner Authorization**: Explicit written authorization obtained before container startup, database connection, migration, or test execution.
- [ ] **2. Docker Project Verified**: Confirm test container name is `webapitgbot_test_db` on port `5433`.
- [ ] **3. Storage Verified**: Confirm volume is `postgres_test_data` (development `postgres_data` untouched).
- [ ] **4. Role Verified**: Confirm user is `gogaa_test_runner` with `NOSUPERUSER` (development `webapitgbot` untouched).
- [ ] **5. Server Identity Verified**: Engine check confirms `SELECT current_database()` is `webapitgbot_test` and `current_user` is `gogaa_test_runner`.
- [ ] **6. Dev Isolation Verified**: Port 5432 is strictly blocked by the test runner guard.
- [ ] **7. Migrations Applied in Isolation**: Apply EF Core migrations to `webapitgbot_test` on port 5433 only.
- [ ] **8. Smoke Test Batch (Group 1)**: Run the 5 pure HTTP preflight & smoke scenarios first.
- [ ] **9. Result Inspection**: Inspect responses and verify zero unexpected database errors.
- [ ] **10. Stop on Failure**: If any smoke scenario fails, stop execution immediately; do not proceed to concurrency tests.
- [ ] **11. Full Suite Execution**: Only proceed to concurrency and financial integrity tests after explicit sign-off on the smoke batch.
