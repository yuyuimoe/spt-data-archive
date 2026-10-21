#!/usr/bin/env bash
# The measured stack: tests/load/stack/compose.yaml, with the data of the development archive.
#
#   tests/load/scripts/stack.sh up       # build the image, start five containers, restore the data, take the CA
#   tests/load/scripts/stack.sh ca       # print the path of the certificate authority that Caddy signed
#   tests/load/scripts/stack.sh down     # stop the stack, keep its volumes
#   tests/load/scripts/stack.sh destroy  # stop the stack and delete its volumes
#
# Every container is limited to 4 vCPU and 2 GB (tests/load/stack/compose.yaml). The Api, the database,
# Redis and Caddy are the deployment of compose.yaml, so a number from this stack is a number about the
# deployment and not about a developer machine.

set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../../.." && pwd)
compose_file=$here/../stack/compose.yaml
project=sptarchive-load
image=localhost/sptarchive-api:load
dump=${DUMP:-/tmp/load-archive.dump}
source_db=${SOURCE_DB:-sptarchive-dev-db-1}
ca_out=${CA_OUT:-/tmp/caddy-root.crt}

say() { printf '%s\n' "$*" >&2; }

container() {
	podman ps -q \
		--filter "label=com.docker.compose.project=$project" \
		--filter "label=com.docker.compose.service=$1" | head -1
}

network() {
	podman network ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}' | head -1
}

build() {
	say "building $image"
	podman build -q -t "$image" -f "$root/src/SptArchive.Api/Dockerfile" "$root" >/dev/null
}

wait_db() {
	local db
	db=$(container db)
	for _ in $(seq 1 60); do
		if podman exec "$db" pg_isready -U archive_owner -d archive >/dev/null 2>&1; then
			return
		fi
		sleep 1
	done

	say "the database did not become ready"
	exit 1
}

dump_dev() {
	if [ ! -f "$dump" ]; then
		say "dumping $source_db to $dump"
		podman exec "$source_db" pg_dump -U archive_owner -d archive -Fc -Z 1 >"$dump"
	fi
}

restore() {
	local db
	db=$(container db)
	dump_dev
	say "restoring $dump"
	podman exec -i "$db" pg_restore -U archive_owner -d archive --clean --if-exists --no-owner <"$dump" >/dev/null

	# The read-only role of docker/initdb, and the extension that says which statement cost the time.
	podman exec -i "$db" psql -q -U archive_owner -d archive <<'SQL' >/dev/null
GRANT CONNECT ON DATABASE archive TO archive_reader;
GRANT USAGE ON SCHEMA public TO archive_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO archive_reader;
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
SQL

	say "data: $(podman exec "$db" psql -tAc 'select count(*) from items' -U archive_owner -d archive) items"
}

take_ca() {
	for _ in $(seq 1 60); do
		if podman cp "$(container caddy):/data/caddy/pki/authorities/local/root.crt" "$ca_out" 2>/dev/null; then
			say "certificate authority: $ca_out"
			return
		fi

		sleep 1
	done

	say "Caddy has no certificate authority yet"
	exit 1
}

up() {
	build
	podman compose -f "$compose_file" up -d
	wait_db
	restore
	take_ca
	say "stack up on https://127.0.0.1:18443/graphql (network $(network))"
}

case ${1:-} in
up) up ;;
ca) take_ca ;;
data) restore ;;
net) network ;;
down) podman compose -f "$compose_file" down ;;
destroy) podman compose -f "$compose_file" down --volumes ;;
*) say "usage: $(basename "$0") up|ca|data|net|down|destroy"; exit 2 ;;
esac
