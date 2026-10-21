#!/usr/bin/env bash
# Runs every check that the repository defines, in order, and stops at the first failure.
# One command proves that a branch is clean. The pipeline in .github/workflows/ci.yml calls this
# script and nothing else, so the set of checks lives here, in Git.
#
# Usage:  scripts/ci.sh            (no arguments)
#
# Needs:
#   * the .NET SDK of global.json / Directory.Build.props (net10.0), Python 3, Node.js with npx
#   * a PostgreSQL 17 that holds the owner role and the read-only role of docker/initdb.
#     The tests migrate that database and roll their work back, so use a throwaway one, never the
#     database that holds archived data.
#   * these two variables, which this script does not guess (an unset one stops the run):
#       ARCHIVE_TEST_CONNECTION          the owner, for example
#         "Host=127.0.0.1;Port=5436;Database=archive;Username=archive_owner;Password=dev_owner_password"
#       ARCHIVE_TEST_READER_CONNECTION   the role archive_reader, same server
#
# Optional:
#   ARCHIVE_CI_PYTHON   the Python interpreter for the docs check. When unset, the script uses
#                       python3 if it can import gherkin, and otherwise builds .venv from
#                       scripts/requirements.txt (a system Python is often externally managed, PEP 668).
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

fail() {
    echo "ci.sh: $*" >&2
    exit 1
}

# 1. The database variables. Without them the tests would use port 5432, which may hold real data.
for variable in ARCHIVE_TEST_CONNECTION ARCHIVE_TEST_READER_CONNECTION; do
    if [[ -z "${!variable:-}" ]]; then
        fail "$variable is not set. The tests need a throwaway PostgreSQL 17 with the roles of docker/initdb; see the header of this script."
    fi
done

# 2. The Python of the docs check.
docs_python() {
    if [[ -n "${ARCHIVE_CI_PYTHON:-}" ]]; then
        echo "$ARCHIVE_CI_PYTHON"
    elif python3 -c "import gherkin" 2>/dev/null; then
        echo python3
    else
        if [[ ! -x .venv/bin/python ]]; then
            python3 -m venv .venv >&2
        fi
        .venv/bin/python -m pip install --quiet --requirement scripts/requirements.txt >&2
        echo .venv/bin/python
    fi
}

summary=()

# step NAME COMMAND...  Runs one check, prints its name first, and stops the script if it fails.
step() {
    local name="$1"
    shift
    echo
    echo "==> $name"
    local started=$SECONDS
    if ! "$@"; then
        echo >&2
        echo "ci.sh: FAILED at '$name'. The checks after it did not run." >&2
        exit 1
    fi
    summary+=("ok  $((SECONDS - started))s  $name")
}

python_bin="$(docs_python)"

step "dotnet build SptArchive.slnx" dotnet build SptArchive.slnx
step "dotnet test SptArchive.slnx" dotnet test SptArchive.slnx
step "check-docs.py" "$python_bin" scripts/check-docs.py
step "likec4 validate docs/architecture/c4" npx --yes likec4 validate docs/architecture/c4

echo
echo "==> ci.sh: every check passed"
printf '    %s\n' "${summary[@]}"
