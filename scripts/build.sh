#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="${WEIXIN_DOTNET:-dotnet}"
dotnet_bin="$(command -v "$dotnet_bin")"
dotnet_bin="$(realpath "$dotnet_bin")"
cd "$project_root"
expected="$(sed -n 's/.*"version":"\([^"]*\)".*/\1/p' global.json)"
actual="$("$dotnet_bin" --version)"
if [[ "$actual" != "$expected" ]]; then
    echo "需要 SDK $expected，实际为 $actual" >&2
    exit 1
fi
stamp="$(date +%Y%m%d-%H%M%S)"
evidence="$project_root/artifacts/verification/linux-$stamp"
packages="$project_root/artifacts/packages/linux-x64"
publish="$project_root/artifacts/publish/linux-x64-$stamp"
mkdir -p "$evidence" "$packages" "$publish"
run_dotnet() { "$dotnet_bin" "$@" 2>&1 | tee -a "$evidence/build.log"; }
run_dotnet --info
run_dotnet restore Weixin.Channel.slnx --locked-mode
run_dotnet build Weixin.Channel.slnx -c Release --no-restore -warnaserror
run_dotnet test tests/Weixin.Channel.Tests/Weixin.Channel.Tests.csproj -c Release --no-restore --no-build --logger 'trx;LogFileName=weixin.trx' --results-directory "$evidence"
if ! grep -Eq '<Counters[^>]*total="[1-9][0-9]*"' "$evidence/weixin.trx" ||
   ! grep -Eq '<Counters[^>]*failed="0"' "$evidence/weixin.trx" ||
   ! grep -Eq '<Counters[^>]*notExecuted="0"' "$evidence/weixin.trx"; then
    echo '测试为空、失败或包含跳过，不满足验收要求。' >&2
    exit 1
fi
for app in Weixin.Cli Weixin.Web; do
    run_dotnet publish "samples/$app/$app.csproj" -c Release -r linux-x64 --self-contained true \
        -p:RestoreLockedMode=true -o "$publish/$app" -warnaserror
done
WEIXIN_DOTNET="$dotnet_bin" bash "$project_root/scripts/verify-web.sh" "$publish"
cp -R README.md docs third-party "$publish/"
archive="$project_root/artifacts/Weixin.Channel-linux-x64-$stamp.tar.gz"
tar -C "$publish" -czf "$archive" .
sha256sum "$archive" > "$evidence/release.sha256"
run_dotnet pack src/Weixin.Channel/Weixin.Channel.csproj -c Release --no-restore --no-build -o "$packages" -warnaserror
consumer="$(mktemp -d -t weixin-consumer-XXXXXXXX)"
cp global.json "$consumer/global.json"
cp scripts/consumer.csproj.template "$consumer/Consumer.csproj"
cp scripts/consumer.cs.template "$consumer/Program.cs"
package_source="$(printf '%s' "$packages" | sed 's/\&/\&amp;/g; s/</\&lt;/g; s/>/\&gt;/g; s/"/\&quot;/g')"
cat > "$consumer/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-weixin" value="$package_source" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
cd "$consumer"
run_dotnet restore Consumer.csproj --configfile "$consumer/NuGet.Config" --packages "$consumer/packages"
run_dotnet restore Consumer.csproj --locked-mode --configfile "$consumer/NuGet.Config" --packages "$consumer/packages"
run_dotnet run --project Consumer.csproj -c Release --no-restore
printf '独立消费者：%s\n自包含发布：%s\n真机验收：未执行；见 docs/acceptance.md\n' "$consumer" "$archive" > "$evidence/result.txt"
echo "构建、测试、自包含发布和独立 nupkg 消费通过。证据：$evidence"
