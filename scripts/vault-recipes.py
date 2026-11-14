#!/usr/bin/env python3
"""Private stdin consumers for vault_run; commands and paths contain metadata only."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import tempfile


def run_private(argv: list[str], value: str | None = None) -> None:
    result = subprocess.run(argv, input=value, text=True, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError("Consumer refused the operation")


def identifier(value: str) -> str:
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,62}", value):
        raise ValueError("Invalid identifier")
    return value


def password() -> str:
    value = sys.stdin.read()
    if not value.endswith("\n"):
        raise ValueError("Missing stdin terminator")
    value = value[:-1]
    if not value or any(character in value for character in "\r\n\0"):
        raise ValueError("This recipe needs a nonempty single-line secret")
    return value


def database(engine: str, action: str, user: str, client_host: str, value: str) -> None:
    user = identifier(user)
    if engine == "postgres":
        if action == "create":
            run_private(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-c", f'CREATE ROLE "{user}" NOLOGIN'])
        run_private(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-c", f'\\password {user}'], value + "\n" + value + "\n")
        if action == "create":
            run_private(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-c", f'ALTER ROLE "{user}" LOGIN'])
    else:
        if not re.fullmatch(r"[A-Za-z0-9_.:%-]+", client_host):
            raise ValueError("Invalid client host")
        digest = "*" + hashlib.sha1(hashlib.sha1(value.encode()).digest()).hexdigest().upper()
        verb = "CREATE USER" if action == "create" else "ALTER USER"
        run_private(["mariadb", "--batch"], f"{verb} '{user}'@'{client_host}' IDENTIFIED BY PASSWORD '{digest}';\n")


def import_field(path: Path, field: str) -> None:
    field = identifier(field)
    matches = []
    for line in path.read_text().splitlines():
        key, separator, value = line.partition("=")
        if separator and key.strip() == field:
            parsed = shlex.split(value, comments=True, posix=True)
            if len(parsed) != 1 or not parsed[0]:
                raise ValueError("Expected one nonempty env value")
            matches.append(parsed[0])
    if len(matches) != 1:
        raise ValueError("Field missing or duplicated")
    sys.stdout.write(matches[0])


def private_config(path: Path, key: str | None, value: str) -> None:
    if os.name != "posix":
        raise RuntimeError("Run the file recipe on the POSIX target")
    os.umask(0o077)
    if path.is_symlink() or (path.exists() and path.stat().st_uid != os.geteuid()):
        raise ValueError("Run as the file owner and do not replace symlinks")
    contents = value + "\n"
    if key:
        key = identifier(key)
        lines = path.read_text().splitlines() if path.exists() else []
        matches = [index for index, line in enumerate(lines) if line.partition("=")[0].strip() == key]
        if len(matches) > 1:
            raise ValueError("Duplicated config key")
        assignment = key + "=" + shlex.quote(value)
        if matches:
            lines[matches[0]] = assignment
        else:
            lines.append(assignment)
        contents = "\n".join(lines) + "\n"
    descriptor, temporary = tempfile.mkstemp(dir=path.parent)
    try:
        with os.fdopen(descriptor, "w") as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(contents)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def http_request(url: str, value: str) -> None:
    if not (url.startswith("https://") or url.startswith("http://127.0.0.1:")):
        raise ValueError("Use HTTPS, or loopback for a harmless check")
    config = "header = " + json.dumps("Authorization: Bearer " + value) + "\n"
    run_private(["curl", "--disable", "--fail", "--silent", "--show-error", "--config", "-", "--url", url], config)


def container(image: str, command: list[str], pane: str, value: str) -> None:
    argv = ["docker", "run", "--rm", "--label", "cockpit.pane=" + pane, "--env-file"]
    contents = "APP_SECRET=" + value + "\n"
    if sys.platform == "linux":
        run_private([*argv, "/dev/stdin", image, *command], contents)
    elif os.name == "nt" and sys.version_info >= (3, 13):
        with tempfile.TemporaryDirectory(prefix="vault-env-", dir=os.environ["COCKPIT_SCRATCH"]) as directory:
            path = Path(directory) / "env"
            path.write_text(contents)
            run_private([*argv, str(path), image, *command])
    else:
        raise RuntimeError("Use Linux or Windows with Python 3.13+ and COCKPIT_SCRATCH")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="recipe", required=True)
    db = sub.add_parser("db")
    db.add_argument("engine", choices=["mariadb", "postgres"])
    db.add_argument("action", choices=["create", "rotate"])
    db.add_argument("user")
    db.add_argument("--client-host", default="localhost")
    imported = sub.add_parser("import-field")
    imported.add_argument("path", type=Path)
    imported.add_argument("field")
    config = sub.add_parser("config")
    config.add_argument("path", type=Path)
    config.add_argument("--key")
    http = sub.add_parser("http")
    http.add_argument("url")
    docker = sub.add_parser("container")
    docker.add_argument("--pane", required=True)
    docker.add_argument("image")
    docker.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    try:
        if args.recipe == "import-field":
            import_field(args.path, args.field)
            return 0
        value = password()
        if args.recipe == "db":
            database(args.engine, args.action, args.user, args.client_host, value)
        elif args.recipe == "config":
            private_config(args.path, args.key, value)
        elif args.recipe == "http":
            http_request(args.url, value)
        else:
            container(args.image, args.command, args.pane, value)
        print("Applied")
        return 0
    except Exception:
        print("Recipe failed; consumer output suppressed", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
