#!/usr/bin/env python3
"""Upsert one plugin version into a store's index.json (AC — automated store publish).

The store (github.com/raymondkrahwinkel/AI-Cockpit-Plugins) is a separate repo whose index.json
is the catalogue the app reads. Publishing a plugin has always meant hand-editing that file: add a
version entry, bump latestVersion, set the published date. This script does exactly that edit and
nothing else, so the publish workflow can open a reviewable PR instead of a person doing it by hand.

Source-of-truth split (the "metadata gap" the design settled on):
  * Mechanical + identity fields come from the plugin's own plugin.json — id, name, description,
    author, version, abstractionsVersion, minHostVersion. These are what the app enforces.
  * Editorial fields the manifest does not carry — category, icon, homepage, repository, featured —
    are taken from an optional store.json beside plugin.json, else preserved from the existing index
    entry, else left unset for a human to fill in the PR. A brand-new plugin with neither is listed
    with just its identity; the reviewer adds the polish.

Immutability: a version already present in the index is never rewritten (exit code 3). A version
users may already have installed must stay byte-for-byte what its sha256 pinned.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import OrderedDict


# Exit codes the workflow branches on.
EXIT_OK = 0
EXIT_ERROR = 1
EXIT_ALREADY_PRESENT = 3


def version_key(v: str) -> tuple[int, ...]:
    """Sort key from the leading numeric dotted part of a version (prerelease suffix ignored).

    Plugin versions here are plain x.y.z, so this orders them correctly; a trailing '-nightly.4'
    or similar is dropped rather than mis-sorted, which is fine because the store lists releases.
    """
    core = v.split("-", 1)[0].split("+", 1)[0]
    parts: list[int] = []
    for piece in core.split("."):
        try:
            parts.append(int(piece))
        except ValueError:
            parts.append(0)
    return tuple(parts)


def load_json(path: str) -> OrderedDict:
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle, object_pairs_hook=OrderedDict)


def first_present(*values):
    """The first value that is not None and not an empty string."""
    for value in values:
        if value is not None and value != "":
            return value
    return None


def build_entry(existing: OrderedDict | None, store_meta: dict, args) -> OrderedDict:
    """Assemble the plugin's index entry, minus its versions list (added by the caller).

    Field precedence — identity from plugin.json (overridable by store.json), editorial from
    store.json else the existing entry. Keys with no value are omitted to keep the index tidy;
    `featured` is always written because it is a meaningful boolean.
    """
    existing = existing or OrderedDict()

    entry: OrderedDict = OrderedDict()
    entry["id"] = args.id
    entry["name"] = first_present(store_meta.get("name"), args.name, existing.get("name"))

    description = first_present(store_meta.get("description"), args.description, existing.get("description"))
    if description is not None:
        entry["description"] = description

    author = first_present(store_meta.get("author"), args.author, existing.get("author"))
    if author is not None:
        entry["author"] = author

    category = first_present(store_meta.get("category"), existing.get("category"))
    if category is not None:
        entry["category"] = category

    icon = first_present(store_meta.get("icon"), existing.get("icon"))
    if icon is not None:
        entry["icon"] = icon

    homepage = first_present(store_meta.get("homepage"), existing.get("homepage"))
    if homepage is not None:
        entry["homepage"] = homepage

    repository = first_present(store_meta.get("repository"), existing.get("repository"))
    if repository is not None:
        entry["repository"] = repository

    featured = store_meta.get("featured")
    if featured is None:
        featured = existing.get("featured", False)
    entry["featured"] = bool(featured)

    return entry


def build_version(args) -> OrderedDict:
    version: OrderedDict = OrderedDict()
    version["version"] = args.version
    version["path"] = args.path
    if args.abstractions_version is not None:
        version["abstractionsVersion"] = args.abstractions_version
    if args.min_host_version:
        version["minHostVersion"] = args.min_host_version
    if args.sha256:
        version["sha256"] = args.sha256
    if args.notes:
        version["notes"] = args.notes
    return version


def main() -> int:
    parser = argparse.ArgumentParser(description="Upsert a plugin version into a store index.json.")
    parser.add_argument("--index", required=True, help="Path to the store's index.json.")
    parser.add_argument("--id", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--description", default=None)
    parser.add_argument("--author", default=None)
    parser.add_argument("--version", required=True)
    parser.add_argument("--abstractions-version", type=int, default=None)
    parser.add_argument("--min-host-version", default=None)
    parser.add_argument("--sha256", default=None)
    parser.add_argument("--path", required=True, help="Zip path relative to the index (e.g. my-plugin/my-plugin-1.0.0.zip).")
    parser.add_argument("--published", required=True, help="ISO date (YYYY-MM-DD) for the latest version.")
    parser.add_argument("--notes", default=None)
    parser.add_argument("--store-json", default=None, help="Optional store.json with editorial fields.")
    args = parser.parse_args()

    try:
        index = load_json(args.index)
    except FileNotFoundError:
        # A store that has never had an index still gets one, so the very first publish works.
        index = OrderedDict([("plugins", [])])
    except json.JSONDecodeError as error:
        print(f"error: could not parse {args.index}: {error}", file=sys.stderr)
        return EXIT_ERROR

    store_meta: dict = {}
    if args.store_json:
        try:
            store_meta = load_json(args.store_json)
        except FileNotFoundError:
            store_meta = {}
        except json.JSONDecodeError as error:
            print(f"error: could not parse {args.store_json}: {error}", file=sys.stderr)
            return EXIT_ERROR

    plugins = index.get("plugins")
    if plugins is None:
        plugins = []
        index["plugins"] = plugins

    # Find the existing entry (and its position, so an update keeps its place in the list).
    existing_index = next((i for i, p in enumerate(plugins) if p.get("id") == args.id), None)
    existing = plugins[existing_index] if existing_index is not None else None

    versions = list(existing.get("versions", [])) if existing else []
    if any(v.get("version") == args.version for v in versions):
        print(f"{args.id} {args.version} is already in the store index — nothing to publish.")
        return EXIT_ALREADY_PRESENT

    versions.append(build_version(args))
    versions.sort(key=lambda v: version_key(v.get("version", "0")), reverse=True)

    entry = build_entry(existing, store_meta, args)

    latest = versions[0].get("version")

    # The entry-level published date describes the newest version. Only move it when this publish is
    # the newest; back-publishing an older version must not rewrite the latest's date. Written before
    # latestVersion to match the field order the store's index.json already uses.
    if latest == args.version:
        entry["published"] = args.published
    elif existing and existing.get("published"):
        entry["published"] = existing["published"]

    entry["latestVersion"] = latest
    entry["versions"] = versions

    if existing_index is not None:
        plugins[existing_index] = entry
    else:
        plugins.append(entry)

    with open(args.index, "w", encoding="utf-8") as handle:
        json.dump(index, handle, indent=2, ensure_ascii=False)
        handle.write("\n")

    action = "updated" if existing_index is not None else "added"
    print(f"{action} {args.id} {args.version} (latest now {latest}) in {args.index}")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
