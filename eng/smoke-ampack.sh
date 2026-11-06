#!/usr/bin/env sh
# A pack author's whole flow with one published ampack binary (PACK-toolset): keygen, init, lint, build, verify --explain,
# inspect, test, diff, install, list, revoke. Usage: eng/smoke-ampack.sh <ampack binary> <empty work folder>
set -eu
A="$1"; W="$2"
mkdir -p "$W" && cd "$W"
"$A" keygen --out keys --name publisher --prefix io.example --policy trust.json
"$A" keygen --out keys --name revoker --prefix io.example --role revoker --policy trust.json
"$A" init notes --id io.example.notes
"$A" lint notes
"$A" build notes --out notes-1.ampack --key keys/publisher.key.pem
"$A" verify notes-1.ampack --policy trust.json --explain
"$A" inspect notes-1.ampack
"$A" test notes-1.ampack --policy trust.json
sed 's/"version": "0.1.0"/"version": "0.2.0"/' notes/pack.json > notes/pack.json.new && mv notes/pack.json.new notes/pack.json
"$A" build notes --out notes-2.ampack --key keys/publisher.key.pem
"$A" diff notes-1.ampack notes-2.ampack --policy trust.json
"$A" install notes-2.ampack --store store --policy trust.json
"$A" list --store store
if "$A" install notes-1.ampack --store store --policy trust.json; then echo "SMOKE FAIL: a downgrade installed"; exit 1; fi
"$A" revoke revocations.json --key keys/revoker.key.pem --expires 2099-01-01T00:00:00Z
"$A" verify notes-2.ampack --policy trust.json --revocations revocations.json
echo "SMOKE OK"
