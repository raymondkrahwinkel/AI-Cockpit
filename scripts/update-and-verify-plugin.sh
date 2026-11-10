#!/usr/bin/env bash
set -euo pipefail

args=(
  --index "$STORE/index.json"
  --id "$(jq -r '.id' "$MANIFEST")"
  --name "$(jq -r '.name' "$MANIFEST")"
  --description "$(jq -r '.description // empty' "$MANIFEST")"
  --author "$(jq -r '.author // empty' "$MANIFEST")"
  --version "$VERSION"
  --min-host-version "$MIN_HOST"
  --sha256 "$SHA"
  --path "$REL"
  --published "$(date -u +%Y-%m-%d)"
)

abs="$(jq -r '.abstractionsVersion // empty' "$MANIFEST")"
if [ -n "$abs" ]; then args+=(--abstractions-version "$abs"); fi
if [ -n "$NOTES" ]; then args+=(--notes "$NOTES"); fi
for package in $PACKAGES; do args+=(--package "$package"); done
while IFS= read -r keyword; do args+=(--keyword "$keyword"); done < <(jq -r '.keywords // [] | .[]' "$MANIFEST")
if [ -f "$DIR/store.json" ]; then
  args+=(--store-json "$DIR/store.json")
  echo "Using editorial fields from $DIR/store.json"
fi

python3 scripts/store-index-upsert.py "${args[@]}"

zip_path="$STORE/$REL"

actual="$(sha256sum "$zip_path" | cut -d' ' -f1)"
if [ "$actual" != "$SHA" ]; then
  echo "::error::The zip on disk hashes to $actual but the run recorded $SHA."
  exit 1
fi

claimed="$(jq -r --arg id "$PLUGIN" --arg v "$VERSION" \
  '.plugins[]? | select(.id==$id) | .versions[]? | select(.version==$v) | .sha256' "$STORE/index.json")"
if [ "$claimed" != "$actual" ]; then
  echo "::error::index.json claims sha256 $claimed for $PLUGIN $VERSION; the zip hashes to $actual."
  exit 1
fi

listing="$(unzip -Z1 "$zip_path")"
if ! grep -qx 'plugin.json' <<<"$listing"; then
  echo "::error::The zip has no plugin.json at its root — the installer would find no manifest."
  exit 1
fi
for key in entryAssembly uiAssembly; do
  assembly="$(jq -r --arg key "$key" '.[$key] // empty' "$MANIFEST")"
  if [ -n "$assembly" ] && ! grep -qxF "$assembly" <<<"$listing"; then
    echo "::error::The zip has no $assembly at its root, the assembly plugin.json names as its $key."
    exit 1
  fi
done

highest="$(jq -r --arg id "$PLUGIN" '.plugins[]? | select(.id==$id) | .versions[]?.version' "$STORE/index.json" | sort -V | tail -1)"
if [ "$highest" != "$VERSION" ]; then
  echo "::error::$VERSION is not the newest version of $PLUGIN in the index ($highest is). The store offers only the newest, so this would reach nobody."
  exit 1
fi

echo "Verified: $PLUGIN $VERSION · sha256 $actual · manifest and its named assemblies present · newest in the index."

if [ -n "$PACKAGES" ]; then
  bash scripts/verify-plugin-packages.sh "$STORE" "$PLUGIN" "$VERSION" "$SUBPROCESS"
fi
