#!/usr/bin/env bash
set -euo pipefail

jqr() {
  jq "$@" | tr -d '\r'
}

resolve_plugin() {
  PLUGIN="$1"
  DIR=""
  for manifest in plugins-dev/Cockpit.Plugin.*/plugin.json; do
    if [ "$(jqr -r '.id' "$manifest")" = "$PLUGIN" ]; then
      DIR="$(dirname "$manifest")"
      break
    fi
  done
  if [ -z "$DIR" ]; then
    echo "::error::No plugin with id '$PLUGIN' found under plugins-dev/."
    return 1
  fi
  MANIFEST="$DIR/plugin.json"
  VERSION="$(jqr -r '.version' "$MANIFEST")"
  MIN_HOST="$(jqr -r '.minHostVersion // ""' "$MANIFEST")"
  RIDS=""
  SUBPROCESS=""
  if [ -f "$DIR/store.json" ]; then
    RIDS="$(jqr -r '.runtimeIdentifiers // [] | join(" ")' "$DIR/store.json")"
    SUBPROCESS="$(jqr -r '.subprocess // empty' "$DIR/store.json")"
  fi
  if [ -n "$RIDS" ] && [ -z "$SUBPROCESS" ]; then
    echo "::error::$DIR/store.json names runtimeIdentifiers but no subprocess."
    return 1
  fi
  WINDOWS="$(printf '%s\n' $RIDS | grep '^win-' | paste -sd' ' || true)"
  OTHERS="$(printf '%s\n' $RIDS | grep -v '^win-' | paste -sd' ' || true)"
  export PLUGIN DIR MANIFEST VERSION MIN_HOST RIDS SUBPROCESS
}

gate_host() {
  WAIT=false
  if [ -z "$MIN_HOST" ]; then
    echo "Plugin declares no minHostVersion — nothing to gate on."
    return 0
  fi

  stable="$(git tag -l 'v*' | sed 's/^v//' | grep -E '^[0-9]+(\.[0-9]+)*$' | sort -V | tail -1 || true)"

  nightly_base=""
  nsha="$(gh api "repos/${GITHUB_REPOSITORY}/git/ref/tags/nightly" -q .object.sha 2>/dev/null || echo "")"
  if [ -n "$nsha" ]; then
    # The digit anchor excludes the VersionPrefix mentioned in a comment.
    nightly_base="$(git show "$nsha:Directory.Build.props" 2>/dev/null | grep -oP '<VersionPrefix>\K[0-9][^<]*' | head -1 || true)"
  fi

  available="$(printf '%s\n%s\n' "$stable" "$nightly_base" | grep -vE '^$' | sort -V | tail -1 || true)"
  echo "Highest stable release: ${stable:-none} · nightly base: ${nightly_base:-none} · newest available host: ${available:-none}"

  if [ -z "$available" ]; then
    echo "::error::Could not determine any published host version (no v* release, no nightly). Cannot verify the plugin will run."
    return 1
  fi

  lowest="$(printf '%s\n%s\n' "$available" "$MIN_HOST" | sort -V | head -1)"
  if [ "$lowest" != "$MIN_HOST" ]; then
    echo "::error::$PLUGIN needs host >= $MIN_HOST but the newest published host is $available. Publish the app release or nightly that carries the host change first, then re-run this."
    return 1
  fi

  echo "Gate passed: newest published host $available satisfies minHostVersion $MIN_HOST."

  # A nightly's base version can satisfy the number before it contains the required host change.
  if [ -n "$stable" ] && [ "$(printf '%s\n%s\n' "$stable" "$MIN_HOST" | sort -V | head -1)" = "$MIN_HOST" ]; then
    echo "A stable release ($stable) satisfies minHostVersion — no nightly containment check needed."
    return 0
  fi

  if [ -z "$nsha" ]; then
    echo "::error::Only a nightly can satisfy minHostVersion $MIN_HOST, but there is no nightly tag to check."
    return 1
  fi

  # Unrelated commits after the nightly must not block this plugin.
  plugin_sha="$(git log -1 --format=%H -- "$DIR")"
  if [ -z "$plugin_sha" ]; then
    echo "::error::Could not determine the newest commit touching $DIR; cannot verify the published host contains it."
    return 1
  fi

  if ! git merge-base --is-ancestor "$plugin_sha" "$nsha"; then
    # AC-1959: automatic publishing waits for the next nightly; manual publishing reports the failed gate.
    if [ "$EVENT" = "workflow_run" ]; then
      echo "::notice::Nightly $nsha does not contain $plugin_sha (newest commit to $DIR) yet. $PLUGIN waits for the next Nightly, which publishes it."
      WAIT=true
      return 0
    fi
    echo "::error::The newest published host is nightly $nsha, which does not contain $plugin_sha — the newest commit to $DIR. Its base version satisfies minHostVersion $MIN_HOST, but that build predates the code being published, so a plugin leaning on a host change would install and quietly do nothing. Run the Nightly on this commit first, then re-run this."
    return 1
  fi

  echo "Containment check passed: nightly $nsha contains $plugin_sha (newest commit to $DIR)."
}

package_plugin() {
  PACKAGES=""
  out="$DIR/bin/Release/net10.0"

  if [ -z "$RIDS" ]; then
    rel="$PLUGIN/$PLUGIN-$VERSION.zip"
    dest="$STORE/$rel"
    mkdir -p "$(dirname "$dest")"
    rm -f "$dest"

    ( cd "$out" && zip -r -X "$dest" . -x '*.pdb' >/dev/null )

    sha="$(sha256sum "$dest" | cut -d' ' -f1)"
    echo "Packaged $rel ($(du -h "$dest" | cut -f1)), sha256 $sha"
    REL="$rel"
    SHA="$sha"
    return 0
  fi

  set -euo pipefail
  sub_dir="$(dirname "$SUBPROCESS")"
  packages=""
  rel=""
  for rid in $RIDS; do
    rid_rel="$PLUGIN/$PLUGIN-$VERSION-$rid.zip"
    dest="$STORE/$rid_rel"
    stage="$RUNNER_TEMP/package-$PLUGIN-$rid"
    mkdir -p "$(dirname "$dest")"
    rm -rf "$dest" "$stage"

    cp -a "$out" "$stage"
    rm -rf "${stage:?}/$sub_dir"
    cp -a "subprocess/$PLUGIN/$rid" "$stage/$sub_dir"
    case "$rid" in win-*) ;; *) chmod +x "$stage/$SUBPROCESS" ;; esac

    ( cd "$stage" && zip -r -X "$dest" . -x '*.pdb' >/dev/null )

    rid_sha="$(sha256sum "$dest" | cut -d' ' -f1)"
    echo "Packaged $rid_rel ($(du -h "$dest" | cut -f1)), sha256 $rid_sha"
    packages="$packages $rid=$rid_rel:$rid_sha"
    if [ "$rid" = "win-x64" ]; then
      rel="$rid_rel"
      sha="$rid_sha"
    fi
  done

  if [ -z "$rel" ]; then
    echo "::error::runtimeIdentifiers has no win-x64, the package the index's path/sha256 repeat."
    return 1
  fi
  REL="$rel"
  SHA="$sha"
  PACKAGES="${packages# }"
}

restore_batch() {
  platform="$1"
  solution="$GITHUB_WORKSPACE/.publish-$platform.slnx"
  rids=""
  printf '<Solution>\n' > "$solution"
  while IFS= read -r plugin; do
    if ! resolve_plugin "$plugin"; then continue; fi
    if [ "$platform" = windows ]; then
      project=("$DIR/$(dirname "$SUBPROCESS")/"*.csproj)
      rids="$rids $WINDOWS"
    else
      project=("$DIR/"*.csproj)
      rids="$rids $RIDS"
    fi
    printf '  <Folder Name="/%s/"><Project Path="%s" /></Folder>\n' "$PLUGIN" "${project[0]}" >> "$solution"
  done < <(jqr -r '.[]' <<< "$PLUGINS")
  printf '</Solution>\n' >> "$solution"
  rids="$(printf '%s\n' $rids | sort -u | paste -sd';')"
  RuntimeIdentifiers="$rids" dotnet restore "$solution" -p:Configuration=Release -p:SelfContained=true
}

publish_subprocesses() {
  for rid in $1; do
    dotnet publish "$DIR/$(dirname "$SUBPROCESS")" --configuration Release --no-restore \
      -r "$rid" --self-contained -warnaserror -o "subprocess/$PLUGIN/$rid"
  done
}

prepare_plugin() {
  resolve_plugin "$1"
  gate_host
  if [ "$WAIT" = true ]; then
    echo deferred > "$RUNNER_TEMP/result-$PLUGIN"
    return
  fi
  if jqr -e --arg id "$PLUGIN" --arg v "$VERSION" \
    '.plugins[]? | select(.id==$id) | .versions[]? | select(.version==$v)' store/index.json >/dev/null; then
    echo already-published > "$RUNNER_TEMP/result-$PLUGIN"
    return
  fi
  if [ -n "$WINDOWS" ] && [ ! -f "subprocess/$PLUGIN/windows-ready" ]; then
    echo "::error::No complete Windows packages for $PLUGIN."
    return 1
  fi
  dotnet build "$DIR" --configuration Release --no-restore -warnaserror
  publish_subprocesses "$OTHERS"
  STORE="$GITHUB_WORKSPACE/prepared/prepared-plugin-$PLUGIN"
  export STORE
  mkdir -p "$STORE"
  cp store/index.json "$STORE/index.json"
  package_plugin
  export REL SHA PACKAGES
  bash scripts/update-and-verify-plugin.sh
  jqr -n --arg plugin "$PLUGIN" --arg manifest "$MANIFEST" --arg rel "$REL" \
    --arg sha "$SHA" --arg packages "$PACKAGES" --arg notes "$NOTES" \
    --arg subprocess "$SUBPROCESS" '$ARGS.named' > "$STORE/publish.json"
  rm "$STORE/index.json"
  echo success > "$RUNNER_TEMP/result-$PLUGIN"
  {
    echo "version=$VERSION"
    echo "min_host=$MIN_HOST"
    echo "sha=$SHA"
  } >> "$GITHUB_OUTPUT"
}

run_batch() {
  platform="$1"
  count=0
  failed=0
  mkdir -p results
  : > "results/$platform.tsv"
  if ! bash "$0" restore "$platform"; then
    echo "::warning::Batch restore reported failures; each plugin will report whether its own assets are usable."
  fi
  while IFS= read -r plugin; do
    output="$RUNNER_TEMP/output-$platform-$plugin"
    : > "$output"
    if GITHUB_OUTPUT="$output" bash "$0" "$platform" "$plugin"; then
      status="$(cat "$RUNNER_TEMP/result-$plugin")"
      if [ "$status" = success ]; then count=$((count + 1)); fi
      cat "$output" >> "$GITHUB_OUTPUT"
    else
      status=failed
      failed=$((failed + 1))
      # A failed verification must never leave a candidate the writer could pick up.
      if [ "$platform" = linux ]; then rm -rf "prepared/prepared-plugin-$plugin"; fi
      echo "::error::$plugin failed during $platform preparation; continuing."
    fi
    printf '%s\t%s\n' "$plugin" "$status" | tee -a "results/$platform.tsv"
  done < <(jqr -r '.[]' <<< "$PLUGINS")
  {
    printf '| Plugin | Result |\n| --- | --- |\n'
    awk -F '\t' '{printf "| %s | %s |\n", $1, $2}' "results/$platform.tsv"
  } >> "$GITHUB_STEP_SUMMARY"
  echo "count=$count" >> "$GITHUB_OUTPUT"
  echo "failed=$failed" >> "$GITHUB_OUTPUT"
}

NOTES="${NOTES:-}"
export NOTES
case "${1:?command required}" in
  info)
    resolve_plugin "$2"
    {
      echo "windows=$WINDOWS"
      echo "version=$VERSION"
      echo "min_host=$MIN_HOST"
    } >> "$GITHUB_OUTPUT"
    ;;
  restore) restore_batch "$2" ;;
  batch) run_batch "$2" ;;
  windows)
    resolve_plugin "$2"
    publish_subprocesses "$WINDOWS"
    touch "subprocess/$PLUGIN/windows-ready"
    echo success > "$RUNNER_TEMP/result-$PLUGIN"
    ;;
  linux) prepare_plugin "$2" ;;
  *) echo "Unknown publish command: $1" >&2; exit 2 ;;
esac
