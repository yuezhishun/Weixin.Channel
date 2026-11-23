#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="$(command -v "${WEIXIN_DOTNET:-dotnet}")"
dotnet_bin="$(realpath "$dotnet_bin")"
if [[ $# -gt 0 ]]; then
    web=("$1/Weixin.Web/Weixin.Web")
    cli=("$1/Weixin.Cli/Weixin.Cli")
else
    web=("$dotnet_bin" "$project_root/samples/Weixin.Web/bin/Release/net11.0/Weixin.Web.dll")
    cli=("$dotnet_bin" "$project_root/samples/Weixin.Cli/bin/Release/net11.0/Weixin.Cli.dll")
fi
if (echo > /dev/tcp/127.0.0.1/3083) >/dev/null 2>&1; then
    echo '127.0.0.1:3083 已被占用；为避免误操作已有服务，Web smoke 拒绝继续。' >&2
    exit 1
fi
evidence="$project_root/artifacts/verification/web-linux-$(date +%Y%m%d-%H%M%S)"
state="$evidence/state"
mkdir -p "$evidence"
"${web[@]}" --state "$state" > "$evidence/server.log" 2> "$evidence/server-error.log" &
server_pid=$!
trap 'kill "$server_pid" 2>/dev/null || true; wait "$server_pid" 2>/dev/null || true' EXIT
base=http://127.0.0.1:3083
ready=false
for attempt in $(seq 1 50); do
    if ! kill -0 "$server_pid" 2>/dev/null; then echo 'Web启动失败，请检查server-error.log。' >&2; exit 1; fi
    if curl --silent --fail -H 'x-weixin-request: 1' "$base/health" > /dev/null; then ready=true; break; fi
    sleep 0.1
done
if [[ "$ready" != true ]]; then echo 'Web未在5秒内就绪。' >&2; exit 1; fi
check() {
    local path="$1" expected="$2"
    shift 2
    local actual
    actual="$(curl --silent --show-error -D "$evidence/headers.txt" -o "$evidence/body.txt" -w '%{http_code}' "$@" "$base$path")"
    if [[ "$actual" != "$expected" ]]; then echo "$path期望$expected，实际$actual" >&2; cat "$evidence/body.txt" >&2; exit 1; fi
    if [[ "$expected" -ge 400 ]] && ! grep -qi 'content-type: application/problem+json' "$evidence/headers.txt"; then echo '错误未返回ProblemDetails' >&2; exit 1; fi
    echo "$path -> $actual" | tee -a "$evidence/checks.log"
}
check / 200
grep -qi '^content-type: text/html' "$evidence/headers.txt"
check /app.js 200
grep -Eqi '^content-type: (text|application)/javascript' "$evidence/headers.txt"
check /app.css 200
grep -qi '^content-type: text/css' "$evidence/headers.txt"
check /markdown.js 200
grep -Eqi '^content-type: (text|application)/javascript' "$evidence/headers.txt"
check /markdown.css 200
grep -qi '^content-type: text/css' "$evidence/headers.txt"
check / 403 -H 'Host: evil.example:3083'
check /app.js 403 -H 'Origin: https://evil.example'
check /api/logins/missing/qr 403
check /api/accounts/missing/messages 403
check /api/accounts/missing/messages 403 -H 'Content-Type: application/json' -X POST -d '{"ownerId":"test","text":"test"}'
check /health 200 -H 'x-weixin-request: 1'
check /health 403
check /health 403 -H 'x-weixin-request: 1' -H 'Host: evil.example:3083'
check /health 403 -H 'x-weixin-request: 1' -H 'Host: 127.0.0.1:9999'
check /health 403 -H 'x-weixin-request: 1' -H 'Origin: https://evil.example'
check /health 200 -H 'x-weixin-request: 1' -H "Origin: $base"
check /api/accounts 200 -H 'x-weixin-request: 1'
grep -qx '\[\]' "$evidence/body.txt"
check /api/inbox/pending 200 -H 'x-weixin-request: 1'
check /api/inbox/claim 204 -H 'x-weixin-request: 1' -X POST
check /api/logins/missing 404 -H 'x-weixin-request: 1'
check /api/logins/missing/code 400 -H 'x-weixin-request: 1' -H 'Content-Type: application/json' -X POST -d '{"code":"abc"}'
check /api/messages 400 -H 'x-weixin-request: 1' -H 'Content-Type: application/json' -X POST -d '{'
check /api/messages/timing 400 -H 'x-weixin-request: 1' -H 'Content-Type: application/json' -X POST -d '{"deliveryId":"test","operation":"test","elapsedMilliseconds":-1}'
check /missing 404 -H 'x-weixin-request: 1'
check /openapi/v1.json 200 -H 'x-weixin-request: 1'
"$dotnet_bin" run --file "$project_root/tests/tools/WebSmokeFixture.cs" -c Release -- "$state" "$evidence" 2>&1 | tee "$evidence/fixture.log"
conflict_exit=0
"${cli[@]}" status --state "$state" > "$evidence/directory-conflict.log" 2>&1 || conflict_exit=$?
if [[ "$conflict_exit" == 0 ]] || ! grep -q '状态目录正被另一个进程使用' "$evidence/directory-conflict.log"; then echo 'CLI/Web目录冲突未明确失败。' >&2; exit 1; fi
kill "$server_pid"
wait "$server_pid" || true
"${cli[@]}" receipts web-receipt-fixture --state "$state" > "$evidence/cli-receipts.json"
grep -q 'receipt-1' "$evidence/cli-receipts.json"
grep -q 'receipt-3' "$evidence/cli-receipts.json"
if grep -q 'FIXTURE_SECRET_MUST_NOT_APPEAR' "$evidence/cli-receipts.json"; then echo 'CLI公开了协议请求或凭据。' >&2; exit 1; fi
echo '首页、HTTP 边界、OpenAPI、账号聊天接口、SSE、租约、持久收据与跨进程目录互斥全部通过，未请求真实微信服务。' > "$evidence/result.txt"
echo "Web验证通过：$evidence"
