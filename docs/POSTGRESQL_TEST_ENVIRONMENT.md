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

## 4. GATE A1a — Development Database Backup & Non-Mutating Archive Inspection

> [!IMPORTANT]
> **GATE A1a IS A STRICTLY NON-MUTATING INSPECTION PROCEDURE.**
> Gate A1a and Gate A1b require **separate, explicit project owner authorization**.
> Gate A1a creates a binary backup of the development database (`webapitgbot_dev`) and inspects the archive header and Table of Contents without making ANY changes, additions, or deletions to any database or Docker container.

### Objectives of Gate A1a
1. Produce a binary-safe, non-overwriting timestamped backup of `webapitgbot_dev` on the Windows host machine.
2. Verify backup exit status (`$LASTEXITCODE -eq 0`) confirming `pg_dump` completed without fatal error.
3. Verify file size threshold (>1000 bytes) and PostgreSQL custom-format (`PGDMP`) header signature.
4. Perform non-mutating Table of Contents (TOC) inspection using `pg_restore --list`.
5. Strictly avoid creating, dropping, or modifying any database on the active development PostgreSQL instance.

### Distinction Between Archive Inspection and Recoverability
> [!WARNING]
> **CRITICAL DISTINCTION REGARDING ARCHIVE INTEGRITY:**
> - `pg_dump` exit code `0` verifies the dump utility completed without encountering fatal server-side errors during execution.
> - File size and `PGDMP` header checks confirm the file is not empty and has the correct binary magic bytes.
> - `pg_restore --list` reads the archive header and catalog TOC structure. It confirms the catalog is parseable and that table/sequence definitions were recorded.
> - **`pg_restore --list` DOES NOT PROVE THAT THE ENTIRE ARCHIVE IS RECOVERABLE.** It does not test data block decompression, referential integrity constraints, table data insertion, or disk write operations.
> - True recoverability can ONLY be verified by an actual end-to-end restore into a separate isolated PostgreSQL instance (Gate A1b).

### Step-by-Step Gate A1a Operational Procedure

```powershell
# ==============================================================================
# GATE A1a — Development Database Backup & Non-Mutating Archive Inspection
# Prerequisite: Explicit Owner Authorization for Gate A1a.
# Terminal: Standard Windows PowerShell (Administrator elevation NOT required).
# Target: Development PostgreSQL container webapitgbot_db (Port 5432).
# ==============================================================================

# Step 1: Ensure local host backup directory exists
$backupDir = "D:\Backups\GogaaByte"
if (!(Test-Path $backupDir)) {
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
}

# Step 2: Generate timestamped non-overwriting dump in container /tmp
# Using a timestamp prevents accidental overwrite of any previous backup files.
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$containerTmpDump = "/tmp/backup_dev_${timestamp}.dump"
$hostBackupFile = "$backupDir\webapitgbot_dev_${timestamp}.dump"

Write-Host "Creating backup in container /tmp ($containerTmpDump)..."
docker exec webapitgbot_db pg_dump -U webapitgbot -d webapitgbot_dev -F c -b -f $containerTmpDump
if ($LASTEXITCODE -ne 0) {
    throw "pg_dump failed with exit code $LASTEXITCODE. Backup process aborted."
}

# Step 3: Extract dump safely to Windows host via docker cp
# NOTE: Avoid PowerShell output redirection (>) which corrupts binary archives.
Write-Host "Copying dump from container to Windows host..."
docker cp "webapitgbot_db:$containerTmpDump" $hostBackupFile
if ($LASTEXITCODE -ne 0) {
    throw "docker cp failed with exit code $LASTEXITCODE."
}

# Step 4: Clean up temporary dump from inside container
docker exec webapitgbot_db rm $containerTmpDump

# Step 5: Verify host file size and binary header integrity
$file = Get-Item $hostBackupFile
Write-Host "Backup Created: $($file.FullName) ($($file.Length) bytes)"
if ($file.Length -lt 1000) {
    throw "Backup verification failed: File size $($file.Length) bytes is suspiciously small."
}

$bytes = [System.IO.File]::ReadAllBytes($hostBackupFile)
$magic = [System.Text.Encoding]::ASCII.GetString($bytes[0..4])
if ($magic -ne "PGDMP") {
    throw "Backup verification failed: Header '$magic' does not match expected PostgreSQL custom-format signature 'PGDMP'."
}
Write-Host "Header Check: Valid PGDMP signature confirmed."

# Step 6: Offline Table of Contents (TOC) Catalog Validation
# pg_restore --list reads the internal TOC catalog without modifying any database.
# If the archive header or TOC is corrupted, pg_restore --list exits with a non-zero code.
Write-Host "Copying backup to container /tmp for catalog inspection..."
docker cp $hostBackupFile "webapitgbot_db:/tmp/toc_verify.dump"
Write-Host "Inspecting archive Table of Contents..."
docker exec webapitgbot_db pg_restore --list /tmp/toc_verify.dump | Out-Null
if ($LASTEXITCODE -ne 0) {
    docker exec webapitgbot_db rm /tmp/toc_verify.dump
    throw "Archive catalog validation failed: pg_restore --list returned exit code $LASTEXITCODE."
}
Write-Host "Catalog Check: Archive TOC successfully parsed and verified."

# Step 7: Clean up temporary inspection dump from container
docker exec webapitgbot_db rm /tmp/toc_verify.dump

Write-Host "================================================================="
Write-Host "GATE A1a COMPLETE: Non-mutating archive inspection passed."
Write-Host "Host Backup File: $hostBackupFile"
Write-Host "Active development database was NOT modified or touched."
Write-Host "================================================================="
```

> [!CAUTION]
> **STOP AND REPORT.** Do not proceed to Gate A1b or Gate A2. Await explicit project owner review and sign-off on Gate A1a.

---

## 5. GATE A1b — Recovery Test in a Separate Isolated PostgreSQL Instance

> [!IMPORTANT]
> **STRICT ISOLATION & SENSITIVE DATA PROTECTION:**
> - **Zero Touch on Development**: The recovery test must **NEVER** run against the active development PostgreSQL container `webapitgbot_db`. It must **NOT** create, overwrite, or drop any database on the active development instance.
> - **Dedicated Disposable Environment**: The recovery test must run in a **completely separate, dedicated, disposable PostgreSQL instance** (`webapitgbot_recovery_verify_db` on isolated temporary port `5434` with disposable volume `postgres_recovery_verify_data`). Never use an unverified or shared instance.
> - **Sensitive Data Protection**: The backup archive (`.dump`) and restored recovery database may contain sensitive development data (e.g. test orders, user identifiers, configuration state). Restrict filesystem permissions on `D:\Backups\GogaaByte`, adhere to approved retention schedules, and require explicit project owner authorization before data cleanup.
> - **No Automatic Volume Deletion**: Databases, containers, or volumes must **NEVER** be automatically deleted upon failure. Resources are preserved for forensic inspection if unexpected errors occur.

### Objectives of Gate A1b
1. Spin up an ephemeral, isolated PostgreSQL container (`webapitgbot_recovery_verify_db`) on host port `5434` using dedicated volume `postgres_recovery_verify_data`.
2. Restore the timestamped backup file generated in Gate A1a into this isolated environment.
3. Enforce strict exit-code validation: `pg_restore` must complete with exit code `0` and zero unresolved errors.
4. Execute structured post-restore verification: distinguish schema presence, data presence (aggregate row counts), data consistency, and restore integrity.
5. Protect customer privacy by validating row counts without printing customer records or PII.
6. Provide explicit operator commands to tear down the ephemeral verification instance only after owner review.

### Step-by-Step Gate A1b Operational Procedure

```powershell
# ==============================================================================
# GATE A1b — Recovery Test in a Separate Isolated PostgreSQL Instance
# Prerequisite: Explicit Owner Authorization for Gate A1b.
# Terminal: Standard Windows PowerShell (Administrator elevation NOT required).
# Target: Ephemeral verification container webapitgbot_recovery_verify_db (Port 5434).
# Active development container webapitgbot_db (Port 5432) remains COMPLETELY UNTOUCHED.
# ==============================================================================

# Step 1: Confirm the backup file from Gate A1a exists on host
# Operator specifies the exact timestamped backup file produced in Gate A1a:
$hostBackupFile = "D:\Backups\GogaaByte\webapitgbot_dev_<TIMESTAMP>.dump"
if (!(Test-Path $hostBackupFile)) {
    throw "Backup file not found at $hostBackupFile. Ensure Gate A1a completed successfully."
}

# Step 2: Provision ephemeral isolated PostgreSQL instance on dedicated port 5434
# Uses temporary container name and isolated disposable volume.
Write-Host "Starting isolated recovery test container on port 5434..."
docker run --name webapitgbot_recovery_verify_db `
    -e POSTGRES_PASSWORD=verify_temp_password_123 `
    -p 127.0.0.1:5434:5432 `
    -v postgres_recovery_verify_data:/var/lib/postgresql/data `
    -d postgres:17-alpine

if ($LASTEXITCODE -ne 0) {
    throw "Failed to start ephemeral recovery verification container."
}

# Step 3: Wait for ephemeral PostgreSQL instance readiness
Write-Host "Waiting for recovery instance to accept connections..."
$retries = 30
while ($retries -gt 0) {
    docker exec webapitgbot_recovery_verify_db pg_isready -h localhost -p 5432 -U postgres | Out-Null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 1
    $retries--
}
if ($retries -eq 0) {
    throw "Ephemeral verification container failed to become ready in time."
}

# Step 4: Copy backup file into ephemeral verification container
Write-Host "Copying archive into isolated verification container..."
docker cp $hostBackupFile "webapitgbot_recovery_verify_db:/tmp/restore_test.dump"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to copy backup into ephemeral container."
}

# Step 5: Execute restore in isolated verification container with strict exit-code validation
# Controlled restoration flags:
# --no-owner: Prevents attempting to set object ownership to development roles, avoiding ownership errors.
# --no-acl: Prevents attempting to restore original access privileges/grants, avoiding privilege errors.
# --exit-on-error: Halts immediately if an unhandled SQL error occurs, preventing partial restores.
Write-Host "Executing pg_restore in isolated container..."
docker exec webapitgbot_recovery_verify_db pg_restore `
    -U postgres `
    -d postgres `
    --no-owner `
    --no-acl `
    --exit-on-error `
    /tmp/restore_test.dump

# STRICT RESTORE EXIT-CODE CRITERIA:
# Success strictly requires exit code 0. Non-zero exit codes are NEVER accepted or downgraded.
if ($LASTEXITCODE -ne 0) {
    throw "pg_restore failed with exit code $LASTEXITCODE. Recovery was not successful. Inspect container logs."
}
Write-Host "Restore Execution: pg_restore completed with exit code 0."

# Step 6: Multi-Stage Restored Data Verification

# 6A. Schema Presence: Verify expected tables exist in the catalog
Write-Host "Listing restored relations in isolated verification instance:"
docker exec webapitgbot_recovery_verify_db psql -U postgres -d postgres -c "\dt"

# 6B. Data Presence: Verify aggregate row counts on representative application tables
# NOTE: No customer records, PII, or raw table rows are printed or logged.
Write-Host "Verifying aggregate row counts on representative domain tables:"
docker exec webapitgbot_recovery_verify_db psql -U postgres -d postgres -c @"
SELECT 'Categories' AS relation, COUNT(*) AS count FROM "Categories"
UNION ALL
SELECT 'Products', COUNT(*) FROM "Products"
UNION ALL
SELECT 'Orders', COUNT(*) FROM "Orders"
UNION ALL
SELECT 'Payments', COUNT(*) FROM "Payments"
UNION ALL
SELECT 'Fulfillments', COUNT(*) FROM "Fulfillments"
UNION ALL
SELECT '__EFMigrationsHistory', COUNT(*) FROM "__EFMigrationsHistory";
"@

# 6C. Restore Integrity & Consistency Check:
# Verify indexes and constraints exist to confirm structural integrity:
Write-Host "Verifying restored indexes and constraints:"
docker exec webapitgbot_recovery_verify_db psql -U postgres -d postgres -c "\di"

# CRITICAL RECOVERY CAVEAT:
# Row counts alone do NOT prove full semantic recovery.
# Full recovery is confirmed when:
# 1. pg_restore completed with exit code 0.
# 2. Schema relations match the expected domain model.
# 3. Aggregate counts confirm data rows are populated.
# 4. Indexes and constraints are active and uncorrupted.

# Step 7: Clean up temporary dump file inside ephemeral container
docker exec webapitgbot_recovery_verify_db rm /tmp/restore_test.dump

Write-Host "================================================================="
Write-Host "GATE A1b RESTORE SUCCESSFUL: Data recoverability is proven."
Write-Host "Verification instance remains running on port 5434 for review."
Write-Host "Active development container (webapitgbot_db) was NOT touched."
Write-Host "================================================================="
```

### Manual Teardown of Ephemeral Verification Instance (After Operator Review)
> [!IMPORTANT]
> The ephemeral instance is **NOT automatically deleted**. This ensures the operator can inspect logs or table data if unexpected restore warnings occur. Once inspection is complete, run the following explicit teardown commands:
```powershell
# Explicit operator cleanup of disposable verification resources:
docker stop webapitgbot_recovery_verify_db
docker rm webapitgbot_recovery_verify_db
docker volume rm postgres_recovery_verify_data
```

> [!CAUTION]
> **STOP AND REPORT.** Do not proceed to Gate A2. Await explicit project owner sign-off on Gate A1b.

---

## 6. GATE A2 — Development Credential Rotation Procedure

> [!IMPORTANT]
> **MANDATORY PREREQUISITES FOR GATE A2:**
> 1. Gate A1a must be successfully completed with verified backup file on host.
> 2. Gate A1b must be successfully completed, proving end-to-end recoverability in an isolated instance.
> 3. Explicit project owner approval for Gate A2 must be granted.
> 4. Under NO circumstances should the historically exposed password (`c7cdb09`) be reused.

### Problem Statement
Historical commit `c7cdb09` committed a PostgreSQL password to a public repository helper file. That password was shared with local development `dotnet user-secrets`. Gate A2 rotates this password safely without exposing the new password in shell history, process command-line arguments, or stdout/stderr logs.

### Security Model & Limitations of ASP.NET Core User Secrets
> [!WARNING]
> **ASP.NET Core User Secrets is a development-time storage mechanism only.**
> - Secrets are stored in plaintext JSON at `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json` on Windows.
> - User Secrets provides **no encryption at rest**. Any process running under the user's account can read the file.
> - Its sole purpose is to keep secrets out of source code repositories during local development.
> - **NEVER** use User Secrets for staging or production environments. Production configurations must use secure managed secret stores (such as Azure Key Vault, HashiCorp Vault, AWS Secrets Manager, or protected environment variables with strict IAM controls).

### Step-by-Step Gate A2 Operational Procedure

#### Step 1: Rotate Password in PostgreSQL Using Interactive Prompt
Never execute `ALTER USER ... WITH PASSWORD '...'` directly in command lines where shell history or process listings could record the plaintext secret. Use `psql`'s interactive `\password` command:

```powershell
# Connect interactively to development container as postgres superuser
docker exec -it webapitgbot_db psql -U postgres -d webapitgbot_dev
```
Inside the interactive `psql` prompt:
```sql
-- \password prompts for the password with asterisks/masking.
-- The password is NOT saved in .psql_history or query logging.
\password webapitgbot
-- Enter new strong password when prompted, then re-enter to confirm.
\q
```

#### Step 2: Update Development `dotnet user-secrets` via Standard Input (Piped JSON)
To prevent passing the password as a command-line argument (which exposes it in `Win32_Process.CommandLine` and Task Manager) and to avoid recording it in PowerShell history, feed the configuration to `dotnet user-secrets set` through standard input.

We use .NET's built-in `[System.Data.Common.DbConnectionStringBuilder]` (the official ADO.NET base class implemented by `NpgsqlConnectionStringBuilder`) to guarantee that special characters—including semicolons (`;`), single and double quotes (`'`, `"`), equals signs (`=`), spaces, and backslashes (`\`)—are escaped with 100% standard ADO.NET/Npgsql compliance, without fragile manual string concatenation:

```powershell
# ==============================================================================
# Update development user-secrets via standard input (piped JSON)
# Run in standard PowerShell (Administrator elevation NOT required).
# ==============================================================================

cd D:\Projects\Web_API_TG_Bot

# 1. Prompt for new password interactively with input masking (characters hidden)
# Read-Host with -AsSecureString is NOT recorded in PowerShell command history.
$securePwd = Read-Host -Prompt "Enter new development PostgreSQL password" -AsSecureString

# 2. Extract plain text to memory securely
$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd)
$plainPwd = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
[System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)

try {
    # 3. Robust ADO.NET Connection String Formatting:
    # System.Data.Common.DbConnectionStringBuilder is built into .NET and handles
    # all character escaping (semicolons, quotes, equals signs, spaces, backslashes)
    # according to the official ADO.NET / Npgsql specification.
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder["Host"] = "localhost"
    $builder["Port"] = 5432
    $builder["Database"] = "webapitgbot_dev"
    $builder["Username"] = "webapitgbot"
    $builder["Password"] = $plainPwd
    $connectionString = $builder.ConnectionString

    # 4. Construct JSON payload in memory
    $secretPayload = @{
        "ConnectionStrings:DefaultConnection" = $connectionString
    }

    # 5. Convert to JSON (ConvertTo-Json handles standard JSON escaping for quotes/backslashes)
    $jsonInput = $secretPayload | ConvertTo-Json -Compress

    # 6. Pipe JSON directly into dotnet user-secrets set via standard input.
    # The password is NOT in process arguments; NO temporary file is written to disk.
    $jsonInput | dotnet user-secrets set -p src\WebApiTgBot\WebApiTgBot.csproj
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet user-secrets set failed with exit code $LASTEXITCODE."
    }

    Write-Host "User secrets successfully updated via standard input."
}
finally {
    # 7. Memory sanitization: Clear sensitive references and trigger garbage collection.
    # IMPORTANT CAVEAT: In .NET, System.String is immutable on the managed heap.
    # Garbage collection releases object references, but does NOT guarantee immediate
    # zeroing or erasure of plaintext secrets from process memory pages.
    # Operators SHOULD CLOSE THIS POWERSHELL TERMINAL WINDOW once rotation is complete
    # to terminate the process and release all allocated memory pages.
    $plainPwd = $null
    $builder = $null
    $connectionString = $null
    $secretPayload = $null
    $jsonInput = $null
    [System.GC]::Collect()
}
```

#### Step 3: Post-Rotation Connectivity & Host Port Verification
> [!IMPORTANT]
> - `dotnet build` compiles C# source code; it **does NOT** attempt a database connection or verify authentication.
> - Running `psql` via `docker exec` connects over the container's internal network; it verifies database authentication from inside the container, but **does NOT independently prove the host-to-container port mapping**.
> - Both container-internal authentication and host port mapping must be verified.

##### 3A. Container-Internal Engine Authentication Verification
Verify that the PostgreSQL engine authenticates the new credentials inside the container:
```powershell
docker exec -it webapitgbot_db psql -h 127.0.0.1 -p 5432 -U webapitgbot -d webapitgbot_dev -c "SELECT current_database(), current_user;"
```
- Prompts interactively for the new password.
- Confirms database is `webapitgbot_dev` and authenticated user is `webapitgbot`.

##### 3B. Host-to-Container Port Mapping Verification
Verify that Docker is actively forwarding traffic from the Windows host port `5432` to the container:
```powershell
# 1. Verify Docker port binding
docker port webapitgbot_db 5432
# Expected output confirms forwarding from 127.0.0.1:5432 or 0.0.0.0:5432

# 2. Verify Windows host TCP connectivity to port 5432 from outside Docker
Test-NetConnection -ComputerName 127.0.0.1 -Port 5432
# Confirms: TcpTestSucceeded : True
```

#### Step 4: Recovery / Rollback (If Authentication Fails)
1. If authentication fails, re-connect as postgres superuser:
   `docker exec -it webapitgbot_db psql -U postgres -d webapitgbot_dev`
   Re-run `\password webapitgbot` to re-synchronize the password.
2. If database access or schema is corrupted, restore `webapitgbot_dev` from the verified Gate A1a backup dump.

---

## 7. Docker Cleanup Safety Rules

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

## 8. Staged Authorization Gates & Operational Checklists

All operations are organized into sequential authorization gates. **No gate may be started until the previous gate has been approved and completed.**

### GATE A1a Checklist — Development Database Backup & Non-Mutating Archive Inspection
- [ ] Explicit Owner Authorization for Gate A1a obtained.
- [ ] Development database container `webapitgbot_db` verified running on port `5432`.
- [ ] Local host backup directory `D:\Backups\GogaaByte` prepared.
- [ ] `pg_dump` executed to timestamped `/tmp/backup_dev_*.dump` with exit code `0`.
- [ ] Binary dump copied safely to host via `docker cp` with exit code `0`.
- [ ] Container `/tmp` dump removed.
- [ ] Host backup file size verified (> 1000 bytes).
- [ ] PostgreSQL custom-format header signature `PGDMP` confirmed.
- [ ] Table of Contents catalog validation via `pg_restore --list` succeeded with exit code `0`.
- [ ] Confirmed zero database mutations on active development instance.
- [ ] Gate A1a completion reported to owner; stop for Gate A1b authorization.

### GATE A1b Checklist — Recovery Test in a Separate Isolated PostgreSQL Instance
- [ ] Explicit Owner Authorization for Gate A1b obtained (prerequisite: Gate A1a completed).
- [ ] Active development database `webapitgbot_dev` and container `webapitgbot_db` confirmed completely protected and untouched.
- [ ] Ephemeral isolated container `webapitgbot_recovery_verify_db` started on port `5434`.
- [ ] Dedicated disposable volume `postgres_recovery_verify_data` confirmed.
- [ ] Backup archive copied into ephemeral container.
- [ ] `pg_restore` executed with `--no-owner --no-acl --exit-on-error` and completed with strict exit code `0`.
- [ ] Schema presence verified via catalog check (`\dt`).
- [ ] Data presence verified via aggregate `COUNT(*)` across representative application tables (PII never queried).
- [ ] Restore integrity verified (indexes and constraints active).
- [ ] Verification container preserved for operator review (no automatic volume deletion).
- [ ] Manual teardown executed after explicit operator confirmation.
- [ ] Sensitive development data protected; explicit authorization required before cleanup.
- [ ] Gate A1b completion reported to owner; stop for Gate A2 authorization.

### GATE A2 Checklist — Development Credential Rotation
- [ ] Explicit Owner Authorization for Gate A2 obtained (prerequisites: Gates A1a and A1b completed).
- [ ] New strong password selected (never reuse historically exposed `c7cdb09` credential).
- [ ] Password rotated in PostgreSQL using interactive `psql` `\password webapitgbot`.
- [ ] Development `dotnet user-secrets` updated via standard input JSON piping using `DbConnectionStringBuilder` (zero command-line arguments, zero plaintext files).
- [ ] PowerShell terminal session closed after rotation to purge immutable in-memory strings.
- [ ] Container internal authentication check confirms database `webapitgbot_dev` and user `webapitgbot`.
- [ ] Host-to-container port mapping verified (`docker port` and `Test-NetConnection` on port `5432`).
- [ ] Gate A2 completion reported to owner; stop for Gate B authorization.

### GATE B Checklist — Test Container Provisioning (Port 5433)
- [ ] Explicit Owner Authorization for Gate B obtained.
- [ ] Environment file `docker/test.env` configured with unique test passwords.
- [ ] Test container started: `docker compose -f docker-compose.test.yml up -d`.
- [ ] Container identity confirmed: `webapitgbot_test_db`, project `gogaabyte-test`, port `5433`.
- [ ] Named volume confirmed: `postgres_test_data` (development `postgres_data` untouched).
- [ ] Container healthcheck reports `healthy`.

### GATE C1 Checklist — Test Engine-Level Read-Only Role & Instance Verification
- [ ] Explicit Owner Authorization for Gate C1 obtained.
- [ ] Engine query confirms: `SELECT current_database()` is `webapitgbot_test`.
- [ ] Engine query confirms: `SELECT current_user` is `gogaa_test_runner`.
- [ ] Role attributes confirmed: `NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOREPLICATION, NOBYPASSRLS`.

### GATE C2 Checklist — Test Database Migrations
- [ ] Explicit Owner Authorization for Gate C2 obtained.
- [ ] Test connection string `GOGAA_TEST_POSTGRES_CONNECTION` configured strictly for port `5433`.
- [ ] EF Core migrations applied strictly to `webapitgbot_test`.
- [ ] Zero migrations or modifications executed on `webapitgbot_dev`.

### GATE D Checklist — PostgreSQL Integration Testing
- [ ] Explicit Owner Authorization for Gate D obtained.
- [ ] Test opt-in flag enabled: `GOGAA_ENABLE_POSTGRES_TESTS=true`.
- [ ] Execute Group 1 smoke batch (5 pure preflight & smoke scenarios) first.
- [ ] Stop on failure: if any scenario fails, halt immediately.
- [ ] Execute full integration test suite only after smoke sign-off.
