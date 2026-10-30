#!/usr/bin/env bash
# Builds everything for a release (Linux, needs: dotnet SDK 8, mono reference assemblies (mono-devel), zip).
#   1. prebuilt ManagerAgent.dll (netstandard2.1, against prebuild/stubs)
#   2. source.zip (used by Setup's "Repair")
#   3. ManagerAgent-Setup.exe (.NET Framework 4.7.2 WinForms, both embedded)
set -euo pipefail
cd "$(dirname "$0")"
OUT=dist; WORK=.build
rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "== stubs"
for a in Il2CppInterop.Runtime Il2Cppmscorlib BepInEx.Core BepInEx.Unity.IL2CPP UnityEngine.CoreModule UnityEngine.UIElementsModule; do
  dotnet build "prebuild/stubs/$a/$a.csproj" -c Release -o prebuild/stubs/out -v q
done
echo "== plugin"
dotnet build prebuild/ManagerAgent.Prebuilt.csproj -c Release -o "$WORK/plugin" -v q
cp "$WORK/plugin/ManagerAgent.dll" "$OUT/ManagerAgent.dll"

echo "== agent tests (simulated FM)"
dotnet build tests/AgentTests/AgentTests.csproj -c Release -o tests/AgentTests/bin -v q
dotnet tests/AgentTests/bin/AgentTests.dll | tail -1

echo "== source.zip"
zip -qr "$WORK/source.zip" src ManagerAgent.csproj README.md -x "*/bin/*" "*/obj/*"

echo "== setup exe"
FPO=()
[ -d /usr/lib/mono/4.7.2-api ] && FPO=(-p:FrameworkPathOverride=/usr/lib/mono/4.7.2-api)
dotnet build installer/ManagerAgent.Setup.csproj -c Release -o "$WORK/setup" -v q "${FPO[@]}"
cp "$WORK/setup/ManagerAgent-Setup.exe" "$OUT/"
ls -la "$OUT"
