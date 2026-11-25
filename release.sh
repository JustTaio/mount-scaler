#!/usr/bin/env bash
# Builds and syncs repo.json with the built manifest.
set -euo pipefail
cd "$(dirname "$0")"

./build.sh

python3 - <<'EOF'
import json, time

with open("MountScaler/bin/Release/MountScaler/MountScaler.json", encoding="utf-8-sig") as f:
    built = json.load(f)
with open("repo.json", encoding="utf-8") as f:
    repo = json.load(f)

entry = repo[0]
for key in ("Author", "Name", "InternalName", "AssemblyVersion", "Punchline", "Description",
            "ApplicableVersion", "RepoUrl", "Tags", "DalamudApiLevel"):
    if key in built:
        entry[key] = built[key]
entry["LastUpdate"] = int(time.time())

with open("repo.json", "w", encoding="utf-8") as f:
    json.dump(repo, f, indent=2)
    f.write("\n")

print(f"repo.json -> {entry['Name']} {entry['AssemblyVersion']} (API {entry['DalamudApiLevel']})")
if "YOUR_GITHUB_USERNAME" in json.dumps(entry):
    print("WARNING: repo.json still contains YOUR_GITHUB_USERNAME; replace it before publishing.")
EOF

echo "Package: MountScaler/bin/Release/MountScaler/latest.zip"
