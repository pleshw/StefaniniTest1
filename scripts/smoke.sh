#!/usr/bin/env bash
# End-to-end smoke test of the Orders & Catalog Platform, through the BFF only (the one door the browser uses).
#
#   bash scripts/smoke.sh [BASE_URL] [--up]
#
# BASE_URL defaults to http://localhost:8080. With --up the script first runs `docker compose up -d --build --wait`
# from the repository root, so a clean machine needs only Docker, bash and curl.
#
# What it checks, in order:
#   1. the BFF answers through nginx;
#   2. the development sign-in works for `shopper` and `admin` (antiforgery token in the form, cookies kept by hand);
#   3. an anonymous caller is refused on an admin route (401) and a shopper too (403);
#   4. an order placed as the shopper (X-CSRF and an Idempotency-Key) reaches the admin's live event stream
#      (OrderPlaced.v1) and ends up Confirmed or OnHold;
#   5. the same Idempotency-Key and body answer with the same order number.
#
# The sign-in is the development one (Bff__DevLogin__Enabled), which only the development compose turns on, so this
# script targets that stack. The passwords come from BFF_SHOPPER_PASSWORD and BFF_ADMIN_PASSWORD in the environment,
# else from .env, else from .env.example. Exit status: 0 all steps passed, 1 a step failed, 2 wrong usage.
#
# Cookies are replayed by hand instead of with curl's jar because the session cookie is Secure and the stack is
# reached over plain http; a browser accepts that on localhost and curl's jar does not on every version.

set -u -o pipefail

usage() {
  sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'
}

base_url=""
bring_up=0
for argument in "$@"; do
  case "$argument" in
    --up) bring_up=1 ;;
    -h | --help)
      usage
      exit 0
      ;;
    -*)
      echo "Unknown option: $argument" >&2
      usage >&2
      exit 2
      ;;
    *) base_url="$argument" ;;
  esac
done
base_url="${base_url:-http://localhost:8080}"
base_url="${base_url%/}"

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
stream_pid=""
status=""
body="$work/body"
headers="$work/headers"
stream="$work/stream"

cleanup() {
  if [ -n "$stream_pid" ]; then
    kill "$stream_pid" 2>/dev/null
    wait "$stream_pid" 2>/dev/null
  fi
  rm -rf "$work"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

step() {
  printf '\n==> %s\n' "$1"
}

ok() {
  printf '    ok: %s\n' "$1"
}

fail() {
  printf '\nSMOKE TEST FAILED: %s\n' "$1" >&2
  if [ -s "$body" ]; then
    printf -- '--- last response body (first 600 characters) ---\n' >&2
    head -c 600 "$body" >&2
    printf '\n' >&2
  fi
  if [ -s "$stream" ]; then
    printf -- '--- admin event stream so far (last 20 lines) ---\n' >&2
    tail -n 20 "$stream" >&2
  fi
  exit 1
}

command -v curl >/dev/null 2>&1 || fail "curl is not installed."

# A value from the environment, else .env, else .env.example; the files are read, never sourced.
setting() {
  local name="$1" value file
  value="${!name:-}"
  if [ -n "$value" ]; then
    printf '%s' "$value"
    return
  fi
  for file in "$repo_root/.env" "$repo_root/.env.example"; do
    if [ -f "$file" ]; then
      value="$(grep -E "^${name}=" "$file" | tail -n 1 | cut -d= -f2- | tr -d '\r' | sed -e 's/^"//' -e 's/"$//')"
      if [ -n "$value" ]; then
        printf '%s' "$value"
        return
      fi
    fi
  done
}

# A version 4 UUID from $RANDOM, so no tool beyond bash is needed (uuidgen and /proc are missing on some systems).
new_uuid() {
  printf '%04x%04x-%04x-4%03x-%04x-%04x%04x%04x' \
    "$RANDOM" "$RANDOM" "$RANDOM" "$((RANDOM & 0x0fff))" "$(((RANDOM & 0x3fff) | 0x8000))" "$RANDOM" "$RANDOM" "$RANDOM"
}

# The first string value of a JSON property in a file ("number":"ORD-1" gives ORD-1). The answers are compact JSON.
json_value() {
  grep -o "\"$1\":\"[^\"]*\"" "$2" | head -n 1 | cut -d'"' -f4
}

# Keeps the cookies a response set in a file of name=value lines, one jar per user.
save_cookies() {
  local jar="$1" line pair name
  touch "$jar"
  tr -d '\r' <"$headers" | awk -F': ' 'tolower($1) == "set-cookie" { sub(/^[^:]*: /, ""); print }' |
    while IFS= read -r line; do
      pair="${line%%;*}"
      name="${pair%%=*}"
      awk -F= -v n="$name" '$1 != n' "$jar" >"$jar.tmp" && mv "$jar.tmp" "$jar"
      case "$(printf '%s' "$line" | tr '[:upper:]' '[:lower:]')" in
        *"expires=thu, 01 jan 1970"* | *"max-age=0"*) ;;
        *) printf '%s\n' "$pair" >>"$jar" ;;
      esac
    done
}

cookie_header() {
  local jar="$work/jar-$1"
  if [ -s "$jar" ]; then
    paste -sd';' "$jar" | sed 's/;/; /g'
  fi
}

# call USER METHOD PATH [curl options...]: sets $status, and leaves the answer in $body and $headers.
call() {
  local user="$1" method="$2" path="$3" cookies session=()
  shift 3
  cookies="$(cookie_header "$user")"
  if [ -n "$cookies" ]; then
    session=(-H "Cookie: $cookies")
  fi
  : >"$body"
  : >"$headers"
  status="$(curl -sS -o "$body" -D "$headers" -w '%{http_code}' --max-time 30 -X "$method" \
    ${session[@]+"${session[@]}"} ${1+"$@"} "$base_url$path")" || fail "curl could not reach $method $base_url$path"
  save_cookies "$work/jar-$user"
}

sign_in() {
  local user="$1" password="$2" field token
  [ -n "$password" ] || fail "no password for $user: set BFF_$(printf '%s' "$user" | tr '[:lower:]' '[:upper:]')_PASSWORD or fill .env"
  call "$user" GET /bff/login
  [ "$status" = 200 ] || fail "GET /bff/login answered $status; is the development sign-in on (Bff__DevLogin__Enabled)?"
  field="$(sed -n 's/.*<input name="\(__[^"]*\)" type="hidden" value="\([^"]*\)".*/\1/p' "$body" | head -n 1)"
  token="$(sed -n 's/.*<input name="\(__[^"]*\)" type="hidden" value="\([^"]*\)".*/\2/p' "$body" | head -n 1)"
  if [ -z "$field" ] || [ -z "$token" ]; then
    fail "the sign-in form has no antiforgery token"
  fi
  call "$user" POST /bff/login \
    --data-urlencode "$field=$token" --data-urlencode "returnUrl=/" \
    --data-urlencode "username=$user" --data-urlencode "password=$password"
  [ "$status" = 302 ] || fail "signing in as $user answered $status instead of 302 (wrong password?)"
  call "$user" GET /bff/user
  [ "$status" = 200 ] || fail "after signing in, /bff/user answered $status for $user"
  ok "signed in as $user: $(tr -d '\n' <"$body")"
}

if [ "$bring_up" = 1 ]; then
  step "Start the stack (docker compose up -d --build --wait)"
  command -v docker >/dev/null 2>&1 || fail "docker is not installed."
  (cd "$repo_root" && docker compose up -d --build --wait) || fail "docker compose up failed"
  ok "the stack is up"
fi

step "Wait for the BFF at $base_url"
attempt=0
until [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$base_url/bff/login" 2>/dev/null)" = 200 ]; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 45 ] || fail "$base_url/bff/login did not answer 200 in 90 seconds"
  sleep 2
done
ok "the sign-in page answers"

step "Sign in as the shopper and as the admin"
sign_in shopper "$(setting BFF_SHOPPER_PASSWORD)"
sign_in admin "$(setting BFF_ADMIN_PASSWORD)"

step "Admin routes refuse an anonymous caller and a shopper"
call anonymous GET /bff/admin/orders
[ "$status" = 401 ] || fail "an anonymous GET /bff/admin/orders answered $status instead of 401"
call shopper GET /bff/admin/orders
[ "$status" = 403 ] || fail "a shopper's GET /bff/admin/orders answered $status instead of 403"
call admin GET /bff/admin/orders
[ "$status" = 200 ] || fail "the admin's GET /bff/admin/orders answered $status instead of 200"
ok "401 anonymous, 403 shopper, 200 admin"

step "Pick a product from the catalog"
call shopper GET "/bff/shop/products?limit=1&currency=BRL"
[ "$status" = 200 ] || fail "GET /bff/shop/products answered $status"
sku="$(json_value sku "$body")"
[ -n "$sku" ] || fail "the catalog returned no product (is the seed on? Catalog__Seed__Enabled)"
ok "using SKU $sku"

step "Open the admin's live event stream"
admin_cookies="$(cookie_header admin)"
: >"$stream"
curl -sS -N -i --max-time 150 -H "Cookie: $admin_cookies" "$base_url/bff/admin/events/stream" >"$stream" 2>"$work/stream.err" &
stream_pid=$!
# The BFF only delivers what happens after it subscribed, so the order waits for the first sign of life: the response
# headers, or at the latest the first heartbeat (every 15 seconds).
attempt=0
until grep -qi '^content-type: text/event-stream' "$stream"; do
  attempt=$((attempt + 1))
  if grep -q '^HTTP/[0-9.]* [0-9]*' "$stream" && ! grep -q '^HTTP/[0-9.]* 200' "$stream"; then
    fail "the event stream answered $(grep -m 1 '^HTTP/' "$stream")"
  fi
  [ "$attempt" -lt 40 ] || fail "the event stream did not open in 40 seconds ($(tr -d '\n' <"$work/stream.err"))"
  sleep 1
done
ok "the stream is open"

step "Place an order as the shopper"
key="$(new_uuid)"
order_body="{\"currency\":\"BRL\",\"items\":[{\"sku\":\"$sku\",\"quantity\":1}]}"
place_order() {
  call shopper POST /bff/shop/orders -H 'Content-Type: application/json' -H 'X-CSRF: 1' \
    -H "Idempotency-Key: $key" --data "$order_body"
}
place_order
[ "$status" = 201 ] || fail "POST /bff/shop/orders answered $status instead of 201"
order_id="$(json_value id "$body")"
order_number="$(json_value number "$body")"
if [ -z "$order_id" ] || [ -z "$order_number" ]; then
  fail "the created order has no id or number"
fi
ok "order $order_number ($order_id), Idempotency-Key $key"

step "The order reaches the admin's event stream"
attempt=0
until grep -F "$order_id" "$stream" | grep '"type":"OrderPlaced.v1"' >/dev/null; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 60 ] || fail "no OrderPlaced.v1 for $order_number reached the admin stream in 60 seconds"
  sleep 1
done
ok "OrderPlaced.v1 for $order_number arrived on the stream"

step "The order ends up Confirmed or OnHold"
attempt=0
order_status=""
until [ "$order_status" = Confirmed ] || [ "$order_status" = OnHold ]; do
  call shopper GET "/bff/shop/orders/$order_id"
  [ "$status" = 200 ] || fail "GET /bff/shop/orders/$order_id answered $status"
  order_status="$(json_value status "$body")"
  case "$order_status" in
    Pending | Confirmed | OnHold) ;;
    *) fail "the order is $order_status; expected Confirmed or OnHold (is the worker running?)" ;;
  esac
  attempt=$((attempt + 1))
  [ "$attempt" -lt 90 ] || fail "the order is still $order_status after 90 seconds (is the worker running?)"
  [ "$order_status" = Pending ] && sleep 1
done
ok "$order_number is $order_status"

step "The same Idempotency-Key answers with the same order"
place_order
[ "$status" = 201 ] || [ "$status" = 200 ] || fail "repeating the request answered $status"
[ "$(json_value number "$body")" = "$order_number" ] ||
  fail "repeating the request returned order $(json_value number "$body") instead of $order_number"
ok "the repeat returned $order_number"

printf '\nSMOKE TEST PASSED: %s\n' "$base_url"
