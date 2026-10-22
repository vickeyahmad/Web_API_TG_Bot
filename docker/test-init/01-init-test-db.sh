#!/bin/sh
set -e

# Verify that the required test runner password is provided in the environment
if [ -z "$GOGAA_TEST_POSTGRES_PASSWORD" ]; then
    echo "ERROR: GOGAA_TEST_POSTGRES_PASSWORD environment variable is not set." >&2
    exit 1
fi

echo "Initializing isolated test database and least-privilege test role..."

# Execute bootstrap commands as the container superuser (postgres).
# Heredoc delimiter 'EOSQL' is quoted to prevent shell parameter expansion.
# psql uses \getenv and :'test_runner_password' to safely escape and quote SQL literals,
# completely preventing SQL injection and quote-breaking syntax errors.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'EOSQL'
    \getenv test_runner_password GOGAA_TEST_POSTGRES_PASSWORD

    CREATE DATABASE webapitgbot_test;

    CREATE ROLE gogaa_test_runner WITH
        LOGIN
        NOSUPERUSER
        NOCREATEDB
        NOCREATEROLE
        NOREPLICATION
        NOBYPASSRLS
        PASSWORD :'test_runner_password';

    ALTER DATABASE webapitgbot_test OWNER TO gogaa_test_runner;

    \connect webapitgbot_test

    GRANT ALL ON SCHEMA public TO gogaa_test_runner;
EOSQL

echo "Test database 'webapitgbot_test' and role 'gogaa_test_runner' initialized successfully."
