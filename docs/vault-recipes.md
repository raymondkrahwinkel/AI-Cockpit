# Private vault recipes (AC-2361)

Use references returned by `vault_list`, never values. `DB`, `NEXT` and `TOKEN` below are reference placeholders. Credential encryption must be unlocked. Writes require the provider's default-off **Allow agents to create and update items** setting and the account's native write permission. `vault_run` keeps its approval card. Structured grants match channel (env/stdin/file), verification mode, variable name, reference, exact argv, stdin framing and the optional `updateOnSuccess` destination separately. A verification grant never authorises an output-returning run. Old unstructured grants require fresh approval. Env/file variable names must be ASCII identifiers (`[A-Za-z_][A-Za-z0-9_]*`).

Command-source stdout and stderr are each capped at 65,536 characters. Exceeding either cap terminates the contained command and refuses the write with a fixed error; no captured output is returned. `vault_run` applies the same cap and suppresses all output on overflow, without committing a staged update.

Deploy `scripts/vault-recipes.py` to a trusted path such as `/trusted/vault-recipes.py` on the target using an ordinary nonsecret file transfer. Run SSH without a TTY (`-T`) and with batch authentication (`-o BatchMode=yes`); host-key checking stays on. Use the DB administrator's OS account for DB operations and the intended file owner's account for config. The helper needs Python 3 and the corresponding DB client or curl; a PostgreSQL administrator normally runs it as the `postgres` OS user. It accepts nonempty single-line secrets, compatible with the generator's alphanumeric default.

## 1. Create a remote DB user

Call `vault_write` with `providerId`, `name="app-db"`, `source="generate"` (default 32 characters), then `vault_list` to find `DB`. Call:

```json
{
  "argv": ["ssh", "-T", "-o", "BatchMode=yes", "root@db", "python3 /trusted/vault-recipes.py db mariadb create app_user --client-host app.internal"],
  "env": {}, "stdinReference": "DB", "reason": "Create the application DB user"
}
```

For PostgreSQL, use `postgres@db` and `python3 /trusted/vault-recipes.py db postgres create app_user`. PostgreSQL roles start with NOLOGIN, get a password through psql's `\password`, then get LOGIN. MariaDB uses `IDENTIFIED BY PASSWORD` with a native password verifier, never plaintext SQL. MariaDB's client-host defaults to `localhost`; choose the actual client host explicitly. Database grants and PostgreSQL host access remain separate administrator decisions. Client output is suppressed, including errors. Never use `mariadb -e "...password..."`: that would put the secret in process arguments.

## 2. Rotate safely

Keep `DB` untouched. Generate a **separate** item (for example `app-db-next`) and list its reference as `NEXT`. Call `vault_run` with:

```json
{
  "argv": ["ssh", "-T", "-o", "BatchMode=yes", "root@db", "python3 /trusted/vault-recipes.py db mariadb rotate app_user --client-host app.internal"],
  "env": {}, "stdinReference": "NEXT", "updateOnSuccess": "DB",
  "reason": "Apply the staged password, then update the existing vault field"
}
```

Use `db postgres rotate app_user` as the PostgreSQL administrator for that engine. `updateOnSuccess` copies the exact staged value into the existing field only after successful stdin delivery and exit 0; it never deletes either item. A failed SQL operation leaves the old DB password and vault field intact. A remote operation and a vault update cannot be one transaction: an SSH disconnect can leave remote completion uncertain, and a vault write can fail after remote success. Keep `NEXT` for recovery, verify which credential works using `vault_run`, and retry applying that same value; do not generate another replacement blindly. The operator alone removes obsolete staging items.

Within one Cockpit host, a shared lock keyed by provider and native containing-item identity covers the entire remote apply and vault commit, including competing direct `vault_write` updates and sibling-field references. The target's revision is captured before waiting, checked again under that lock before application and before commit, then checked inside the provider's write gate. A queued stale operation never starts the remote command. A later conflict refuses the vault commit and retains the staged item for recovery. External clients and other Cockpit instances are outside the lock; the native CLIs do not provide a distributed remote/vault transaction or a guaranteed server-side compare-and-swap.

## 3. Import one existing remote field

Call `vault_write`, not `vault_run`, so command stdout stays private:

```json
{
  "providerId": "1password", "name": "imported-db", "source": "command",
  "argv": ["ssh", "-T", "-o", "BatchMode=yes", "root@db", "python3 /trusted/vault-recipes.py import-field /opt/faultline-mmo/secrets/db.env DB_PASSWORD"]
}
```

Only the selected env value is emitted by the helper. A missing, duplicated or empty field fails. The supported env format is one POSIX-quoted or unquoted assignment per line; no shell expansion or multiline values. Never `cat` the entire file or read the field into an agent-visible tool result. Add an existing `reference` only when intentionally updating a vault field in place.

## 4. Write remote config privately

Use `vault_run` with `stdinReference=DB`, `env={}` and argv:

```json
["ssh", "-T", "-o", "BatchMode=yes", "app@host", "python3 /trusted/vault-recipes.py config /srv/app/secrets/db.env --key DB_PASSWORD"]
```

The helper uses umask 077, a mode-0600 sidecar in the destination directory, fsync and atomic replacement. Existing unrelated keys remain intact; duplicate keys and symlinks are refused, and existing files must belong to the caller. Omit `--key` for a dedicated raw secret file. Run as the intended owner rather than replacing another account's config as root. An interrupted replacement leaves the old file intact; an uncatchable kill can leave a private sidecar for operator cleanup.

## 5. Authenticate an HTTP call

Use an existing API token's reference `TOKEN` (or import its command-produced value with recipe 3); a generated random string is only an API token if the service was configured to accept it. Call `vault_run` with `stdinReference=TOKEN`, `env={}` and:

```json
["python3", "scripts/vault-recipes.py", "http", "https://api.example.test/status"]
```

The helper formats a Bearer header into curl config on stdin (`curl -K -`) and suppresses response bodies/errors. The value never appears in argv. HTTPS is required except for loopback smoke checks; redirects are not followed. For another header/authentication scheme, adapt the trusted consumer, keeping values on stdin or in its environment and suppressing output.

## 6. Give a container a secret

Call `vault_run` with `stdinReference=TOKEN`, `env={}` and:

```json
["python3", "scripts/vault-recipes.py", "container", "--pane", "YOUR_PANE_ID", "YOUR_IMAGE", "YOUR_COMMAND"]
```

The child receives `APP_SECRET`. On Linux the helper feeds an env-file through `/dev/stdin`, without a secret file. On Windows use `py` and Python 3.13+ with `COCKPIT_SCRATCH` available: the env-file lives in a private temporary directory and is removed after normal completion or failure. An uncatchable process kill leaves any remainder private until session scratch cleanup. Never use `docker -e APP_SECRET=VALUE`. Containers get `--rm` and the calling pane's label. Docker's own container metadata contains the env value and is local privileged state; do not inspect it into agent output. Long-running consumers need operator-owned lifecycle management.

## Combining channels and limitations

`env` can supply a second reference to a **local wrapper**, alongside stdin. Neither SSH nor `docker exec` automatically forwards the wrapper's env. Forward additional credentials explicitly inside the private consumer, never by constructing secret-bearing argv. The tool masks literal values and common base64/base64url/URL forms in stdout and stderr. Arbitrary fragments, derived hashes and composite encodings are not reliably recognisable; use `verifyOnly` to discard all consumer output. Do not use tracing, verbose credential logging or echo secrets.

## 7. Several fields in one item

Use `vault_write` with `source="command"`, `fieldNames=["username","password","host"]` and a command that emits one private JSON object with exactly those string fields. Field names are declared before execution so a command cannot smuggle a secret into returned field-name metadata. No field values are tool arguments. Generate/fetch the password inside the command; stdout remains private. To update several fields, supply an existing field reference of that item and declare only the fields to change; other fields remain intact. Duplicate or undeclared keys are refused.

1Password stores native fields in one item and updates it once. Bitwarden Secrets Manager has one scalar value per secret, so its plugin stores a JSON bundle in that one value and exposes `#field=NAME` references. Only the multi-field path sets a separate native `note` with the bundle version and validated predeclared field names; listing never discovers metadata by inspecting private scalar contents or private bundle keys. A scalar containing the same JSON shape remains opaque. This is not a collection of separate secrets and has no partial multi-call write. Existing scalar bws secrets are not silently converted into bundles or overwritten wholesale. Reference collisions are refused. Each plugin serializes its own read-modify-write operations so concurrent agent updates preserve sibling fields. External clients and other Cockpit instances are not part of that local lock. Native bws writes still expose the whole updated bundle in local process arguments, as documented by the provider option. The [official bws CLI contract](https://bitwarden.com/help/secrets-manager-cli/) documents `--note` and `revisionDate`.

Bitwarden bundles contain at most 64 fields in total. Updating existing names at the limit is allowed; adding a 65th name is refused before the native write, preserving the original bundle and every field reference. The writer and metadata reader share that ceiling.

Inject any number of the returned field references through env (or files), for example `{"DB_USER":"USER_REF","DB_PASSWORD":"PASSWORD_REF","DB_HOST":"HOST_REF"}`. The child composes its connection string; use `verifyOnly=true` so even a printed/encoded composite cannot come back.

## 8. Multiline and file secrets

Store textual PEM keys/certificates or JSON documents with single-value `source="command"`; stdout is the exact value, including line endings. Multiple fields can hold a TLS certificate and key. Call `vault_run` with `env={}` and `files={"TLS_CERT_FILE":"CERT_REF","TLS_KEY_FILE":"KEY_REF"}`. Those variables contain paths; no secret bytes occur in argv. The file contains exact UTF-8 bytes, without the extra newline used by default stdin.

Linux uses a mode-0600 anonymous `memfd` and a `/proc/<owner-pid>/fd/<fd>` path. No plaintext directory entry is created, and owner death closes the owning descriptor. The child must be able to access the owner's proc-fd path under the same OS identity. Windows uses the per-user Cockpit state directory's `runtime/vault` subdirectory, never the checkout. Before writing any value, the runtime/temporary directory and file get an explicit protected DACL granting only the current user's SID full control, with inheritance disabled. The owner and exact DACL are read back; permission failures refuse the operation. The shared config-file permission helper enforces the same Windows policy and fails closed. Windows delete-on-close handles remove the secret file on normal completion, timeout, child crash and owner process death. Windows readers must allow delete sharing (`FileShare.Delete` in .NET, `os.O_TEMPORARY` with `os.open` in Python); readers without that sharing fail closed and should use stdin. Kernel cleanup cannot revoke a value already copied by an approved consumer, and deletion is not secure erasure of storage media. Empty directory remnants after host death contain no values.

This channel deliberately refuses macOS: the current runner has no equivalent parent-owned anonymous-file path with the same crash guarantee. Env and stdin still work there. Raw binary attachments and provider-native document upload/download APIs are outside this UTF-8 string contract; the requested PEM/JSON/key examples fit textual fields.

## 9. Platform stores and registry config

Use `env={}`, `stdinReference=TOKEN`, `stdinNewline=false` (exact bytes) and `verifyOnly=true` for these argv recipes:

```json
["gh", "secret", "set", "NAME", "--repo", "OWNER/REPO"]
["kubectl", "create", "secret", "generic", "NAME", "--from-file=key=/dev/stdin"]
["docker", "secret", "create", "NAME", "-"]
```

`/dev/stdin` is the Linux kubectl recipe; on Windows a local wrapper opens the injected file path and passes only that path to `--from-file`. All store-specific authentication, permissions and lifecycle remain with their own CLI/account; these tools do not add a new provider to Cockpit. Use a dry-run/dummy receiver for review checks, never a production cluster, Swarm or repository.

Import a complete `.npmrc` privately as an opaque file value, then run `npm ping` with `files={"NPM_CONFIG_USERCONFIG":"NPMRC_REF"}` and `verifyOnly=true`. Registry tokens stay in the config file, never in `--token`/URL arguments. On Windows the client must support delete-sharing, like every file-channel consumer. For a new config, compose it from credentials inside a trusted consumer or provisioning command; do not return the config to the agent.

## 10–13. Coverage and boundaries

| Scenario | Covered / not covered and why |
| --- | --- |
| 7 multi-field | Covered: one private JSON source, predeclared names, atomic native-item update or one bws JSON-bundle update; omitted fields preserved. Several env/file references can be injected together. Native vendor CLI/account writes are not exercised by the local dummy checks. |
| 8 files/multiline | Covered for exact UTF-8 PEM/JSON/text on Windows/Linux, with OS-owned cleanup after timeout/crash/host death. Windows requires delete-sharing; Linux requires accessible proc-fd paths. macOS and binary/document-attachment APIs are not covered for the reasons above. |
| 9 platform stores | Covered by exact stdin or files and `verifyOnly`; no per-service tool option needed. Recipes are documented, transport tested with local dummies. Real GitHub/Kubernetes/Swarm/registry account writes are outside the authorised local-only checks. |
| 10 composite values | Covered: compose inside the child from multiple field references and use `verifyOnly`. No generic connection-string templater is needed. A composite encoded as a whole is not automatically inferred by the masker. |
| 11 verification | Covered: `verifyOnly` returns only `{ok:true}` or `{ok:false}` after execution and has a distinct grant. Preflight denials retain safe reasons; verification is only as meaningful as the consumer's exit status. |
| 12 lifecycle | Covered by value-free timestamp/session/item audit events (`Created`, `Updated`, `Rotated`, requests/failures) and provider history. Not a permanent creator/last-rotation catalog: the host audit is a bounded 500-event ring and cannot attribute external provider edits. No deletion tool or automatic staging-item cleanup. |
| 13 masking | Covered: literal UTF-8 values, standard/base64url padded and unpadded forms, canonical URL/form encoding (including lower-case percent hex), and standard 76-column base64 line wrapping with LF/CRLF. Arbitrary line splitting, JSON escaping, hashes, transformed/composite encodings are not a general DLP guarantee; `verifyOnly` suppresses all output for such consumers. |

## Local evidence

The review checkpoint records the actual recipe checks in Depot `Lessons/VaultWrites.md`. Probes use an in-memory dummy provider and an owned, labelled container with disposable MariaDB/PostgreSQL users and a loopback HTTP server. No production server or FM credential is involved. The repository's existing category-A test covers setting-off refusal, distinct channels, EOF, masking, grant-mode spoofing, opaque bws scalars, declared field metadata, Windows DACLs, concurrent rotations/direct writes, stale revisions and both command-source buffer caps; test-case count is unchanged. The grant-mode exploit was demonstrated red before the fix and green after it. Native provider revision handling is also checked against local fake op/bws CLI responses, without vendor-account writes.
