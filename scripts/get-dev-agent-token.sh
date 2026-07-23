#!/usr/bin/env sh
set -eu

authority="${AKOS_FABRIC_IDENTITY_AUTHORITY:-https://localhost:7101}"
client_id="${AKOS_FABRIC_DEVELOPMENT_CLIENT_ID:-agent-control-development-operator}"
client_secret="${AKOS_FABRIC_DEVELOPMENT_CLIENT_SECRET:-${Identity__Development__ClientSecret:-}}"
scopes="${AKOS_FABRIC_DEVELOPMENT_SCOPES:-agent.sessions.read agent.sessions.create agent.sessions.operate}"
python_bin="${PYTHON_BIN:-python3}"

if [ -z "$client_secret" ]; then
    echo "Set AKOS_FABRIC_DEVELOPMENT_CLIENT_SECRET." >&2
    exit 1
fi

case "$authority" in
    https://*) ;;
    http://localhost:*|http://127.0.0.1:*)
        if [ "${AKOS_FABRIC_ALLOW_HTTP_IDENTITY:-false}" != "true" ]; then
            echo "Refusing HTTP discovery unless AKOS_FABRIC_ALLOW_HTTP_IDENTITY=true." >&2
            exit 1
        fi
        ;;
    *)
        echo "Identity authority must be HTTPS, except for explicitly enabled loopback development." >&2
        exit 1
        ;;
esac

discovery="$(curl --fail --silent --show-error \
    "${authority%/}/.well-known/openid-configuration")"
token_endpoint="$(printf '%s' "$discovery" |
    "$python_bin" -c 'import json,sys; print(json.load(sys.stdin).get("token_endpoint", ""))')"

if [ -z "$token_endpoint" ]; then
    echo "IdentityServer discovery metadata did not contain a token endpoint." >&2
    exit 1
fi

token_response="$(printf '%s' "$client_secret" |
    curl --fail --silent --show-error \
        --request POST \
        --header "Content-Type: application/x-www-form-urlencoded" \
        --data-urlencode "grant_type=client_credentials" \
        --data-urlencode "client_id=$client_id" \
        --data-urlencode "client_secret@-" \
        --data-urlencode "scope=$scopes" \
        "$token_endpoint")"

printf '%s' "$token_response" |
    "$python_bin" -c 'import json,sys
payload=json.load(sys.stdin)
token=payload.get("access_token")
if not token:
    raise SystemExit("IdentityServer did not return an access token.")
print(token)'
