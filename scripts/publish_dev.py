"""Private development publication. No daemon, runner, or production DB test doubles.

The state machine is deliberately separate from Docker/GitHub IO so failures can
be exercised without touching personal data. Run through publish-dev.py.
"""
import argparse
import contextlib
import datetime as dt
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import time
import urllib.request
import uuid

REPOSITORY = "phcastello/Aegis"
REVISION = "org.opencontainers.image.revision"
STATUS = "Aegis private deployment"
# Reviewed Up: additive columns with defaults, new tables and indexes; replaces
# a nonunique ConversationId index with a prefix-compatible composite index.
# The UPDATE initializes NEW columns only. Down is never executed.
SAFE_MIGRATIONS = {
    "20261022133709_ReminderInterventions":
        "b9be5eb44e2fa979234d399c1ab79e3b6775e4813d4441128266e9200e60b5e1",
}


class Blocked(RuntimeError):
    pass


def require(value, message):
    if not value:
        raise Blocked(message)


def save(path, value):
    path = Path(path)
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(value, indent=2) + "\n")
    temp.replace(path)


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def scope(paths):
    result = set()
    for path in paths:
        if path.startswith("backend/") or path.startswith("docker-compose"):
            result.add("backend")
        if path.startswith("frontend/aegis-pwa/") or path.startswith("docker-compose"):
            result.add("pwa")
        if path.startswith("packages/aegis-client/"):
            result.update(("pwa", "native"))
        if path.startswith("apps/aegis-node/"):
            result.add("native")
        if not (path.startswith(("docs/", ".github/", "scripts/", "backend/", "frontend/", "packages/", "apps/"))
                or path.endswith((".md", ".zip")) or path in (".gitignore", ".dockerignore")
                or path.startswith("docker-compose")):
            raise Blocked("Unclassified application dependency: " + path)
    return result


def migration_policy(source, pending):
    for migration in pending:
        path = source / "backend/src/Aegis.Infrastructure/Migrations" / (migration + ".cs")
        require(migration in SAFE_MIGRATIONS and path.is_file()
                and sha256(path) == SAFE_MIGRATIONS[migration],
                "Migration destructive or uncertain; specific review/authorization required: " + migration)


def publish(port, plan, journal):
    """Never advance a feed until private deployment passed. Journal every boundary.

    The port must prepare recovery BEFORE stop/migrate/deploy. Database downgrade
    is deliberately absent. Recovery is allowed only after rehearsal has proved
    the old application accepts the resulting schema.
    """
    changed = False
    private_ready = False
    journal.update(sourceCommit=plan["sha"], components=plan["components"], success=False)
    port.record(journal)
    try:
        port.pin(plan["sha"])
        port.preflight(plan)
        port.record(journal, phase="preflight")
        port.prepare(plan)  # build + disposable tests; no production writes
        port.record(journal, phase="prepared")
        if plan["pending"]:
            port.backup_and_rehearse(plan)
            port.record(journal, phase="backup-verified")
        port.pin(plan["sha"])
        port.unchanged(plan)
        if {"backend", "pwa"} & set(plan["components"]):
            changed = True  # even a failing up may have changed a container
            port.deploy(plan)
        port.validate(plan)
        port.checkpoint(plan)
        private_ready = True
        port.record(journal, phase="private-healthy", migrations=plan["pending"])
        if "native" in plan["components"]:
            port.native(plan)  # existing protected signer/publisher owns the feed
        port.validate(plan)
        port.record(journal, phase="complete", success=True)
    except BaseException as error:
        recovery = "not-needed"
        port.on_failure(plan)
        if private_ready:
            recovery = "healthy-private-deployment-retained"
        elif changed or port.writers_stopped:
            try:
                port.recover(plan)
                recovery = "previous-images-healthy-schema-retained"
            except BaseException:
                recovery = "manual-inspection-required"
        port.record(journal, phase="failed", error=str(error), recovery=recovery, success=False)
        raise
    finally:
        port.cleanup()
    return journal


@contextlib.contextmanager
def locked(state):
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (state / "publish.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise Blocked("Another publish-dev is running on this private host.") from None
        yield


class PrivateHost:
    writers_stopped = False

    def __init__(self, root, state):
        self.root, self.state = root, state
        self.op = state / (dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex[:8])
        self.op.mkdir(mode=0o700)
        self.source = self.op / "source"
        self.disposable = []
        self.network = None
        self.recovery_proved = False
        self.native_promoted = False
        self.native_run = None
        self.migration_attempted = False
        self.images = {}
        self.current = json.loads((state / "current.json").read_text()) if (state / "current.json").exists() else {}

    def run(self, args, *, cwd=None, data=None, output=None, env=None):
        # Never display command output: Compose, pg tools and API errors can
        # contain credentials or personal data. All logs stay owner-only.
        with (self.op / "operations.private.log").open("ab") as log:
            result = subprocess.run(list(map(str, args)), cwd=cwd or self.root,
                                    input=data, stdout=output or subprocess.PIPE,
                                    stderr=log, env=env)
            if result.returncode:
                if result.stdout:
                    log.write(result.stdout)
                raise Blocked(f"{Path(str(args[0])).name} {args[1]} failed; inspect {self.op}/operations.private.log")
            if result.stdout:
                log.write(result.stdout)
            return result.stdout.decode() if result.stdout is not None else ""

    def git(self, *args):
        return self.run(["git", *args]).strip()

    def gh(self, path, method="GET", body=None):
        args = ["gh", "api", "repos/" + REPOSITORY + "/" + path, "--method", method]
        if body is not None:
            args += ["--input", "-"]
        out = self.run(args, data=json.dumps(body).encode() if body is not None else None)
        return json.loads(out) if out.strip() else None

    def inspect(self, name):
        return json.loads(self.run(["docker", "inspect", name]))[0]

    def record(self, journal, **changes):
        journal.update(changes, evidence=str(self.op), updatedAt=dt.datetime.now(dt.timezone.utc).isoformat())
        save(self.op / "result.json", journal)
        print(json.dumps({k: journal[k] for k in ("phase", "success", "evidence") if k in journal}), flush=True)

    def pin(self, sha):
        require(self.git("rev-parse", "--verify", sha + "^{commit}") == sha, "Selected commit changed.")
        if self.source.exists():
            require(self.git("rev-parse", "HEAD") != "", "Repository unavailable.")
            require(self.run(["git", "-C", self.source, "rev-parse", "HEAD"]).strip() == sha,
                    "Candidate checkout differs from selected commit.")
            require(not self.run(["git", "-C", self.source, "status", "--porcelain", "--untracked-files=all"]).strip(),
                    "Candidate checkout was modified after selection.")

    def sql(self, sql, container=None, database=None, user=None):
        env = self.pg_env
        return self.run(["docker", "exec", "-i", container or self.pg["Id"], "psql", "-XAt", "-v", "ON_ERROR_STOP=1",
                         "-U", user or env["POSTGRES_USER"], "-d", database or env["POSTGRES_DB"]], data=sql.encode()).strip()

    @staticmethod
    def identity(container):
        labels = container["Config"].get("Labels") or {}
        return {"sha": labels.get(REVISION), "image": container["Image"]}

    def discover(self, ref, full=False, rebuild=()):
        require(not os.environ.get("DOCKER_HOST") and not os.environ.get("DOCKER_CONTEXT"),
                "Use the local private Docker socket; remote Docker endpoints are not supported.")
        require(self.run(["docker", "context", "show"]).strip() == "default", "Use the local private Docker context.")
        self.git("fetch", "origin", "main", "aegis-distribution")
        if not re.fullmatch(r"[0-9a-f]{40}", ref):
            require(not ref.startswith("-"), "Invalid ref.")
            self.git("check-ref-format", "--branch", ref)
            self.git("fetch", "origin", ref)
            sha = self.git("rev-parse", "FETCH_HEAD^{commit}")
            branch = ref
        else:
            sha = ref
            branch = self.git("branch", "-r", "--contains", sha).splitlines()
            branch = next((x.strip().removeprefix("origin/") for x in branch
                           if x.strip().startswith("origin/") and " -> " not in x and "aegis-distribution" not in x), "")
            require(branch, "Selected SHA must be pushed to a repository branch.")
        self.pin(sha)
        self.api, self.pwa, self.pg = [self.inspect(n) for n in ("aegis-api", "aegis-pwa", "aegis-postgres")]
        self.others = [self.inspect(n) for n in ("aegis-qdrant", "aegis-neo4j")]
        labels = self.api["Config"]["Labels"]
        self.project = labels["com.docker.compose.project"]
        require(all(c["Config"]["Labels"]["com.docker.compose.project"] == self.project
                    for c in [self.pwa, self.pg, *self.others]), "Compose projects differ.")
        self.deploy_root = Path(labels["com.docker.compose.project.working_dir"])
        files = labels["com.docker.compose.project.config_files"].split(",")
        self.dc = ["docker", "compose", "--project-directory", str(self.deploy_root), "-p", self.project]
        envfile = labels.get("com.docker.compose.project.environment_file")
        if envfile:
            self.dc += ["--env-file", envfile]
        for file in files:
            self.dc += ["-f", file]
        self.config = json.loads(self.run([*self.dc, "config", "--format", "json"]))
        save(self.op / "compose-before.private.json", self.config)
        save(self.op / "containers-before.private.json", [self.api, self.pwa, self.pg, *self.others])
        self.pg_env = dict(x.split("=", 1) for x in self.pg["Config"]["Env"])
        self.api_env = dict(x.split("=", 1) for x in self.api["Config"]["Env"])
        require(self.api_env.get("ASPNETCORE_ENVIRONMENT") == "Production", "Private API must be in Production.")
        effective_env = self.config["services"]["aegis-api"]["environment"]
        require(all(self.api_env.get(k) == str(v) for k, v in effective_env.items() if v is not None),
                "Compose environment differs from the running API; reconcile without changing credentials.")
        self.url = self.api_env["AEGIS_PUBLIC_APP_URL"].rstrip("/")
        require(self.url.startswith("https://"), "Private HTTPS endpoint required.")
        for service, container in (("aegis-api", self.api), ("aegis-pwa", self.pwa)):
            # Freeze the actual images for reliable recovery, never a mutable tag.
            self.config["services"][service]["image"] = container["Image"]
            self.config["services"][service].pop("build", None)
        self.frozen = self.op / "previous.compose.private.json"
        save(self.frozen, self.config)
        self.fdc = ["docker", "compose", "--project-directory", str(self.deploy_root), "-p", self.project, "-f", str(self.frozen)]
        feed = self.gh("contents/latest.json?ref=aegis-distribution")
        import base64
        self.feed = json.loads(base64.b64decode(feed["content"]))
        self.feed_blob = feed["sha"]
        require(self.feed.get("compatibility") == {"api": 1, "nodeData": 1}, "Unknown installed client contracts.")
        known = {"backend": self.identity(self.api), "pwa": self.identity(self.pwa),
                 "native": {"sha": self.feed["sourceCommit"]}}
        components = []
        for component, identity in known.items():
            if full or component in rebuild or not identity["sha"] or component in scope(self.git("diff", "--name-only", identity["sha"], sha).splitlines()):
                components.append(component)
        applied = self.sql('SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";').splitlines()
        names = self.git("ls-tree", "-r", "--name-only", sha, "backend/src/Aegis.Infrastructure/Migrations").splitlines()
        migrations = sorted(Path(n).stem for n in names if re.search(r"/\d{14}_[^/]+\.cs$", n) and not n.endswith(".Designer.cs"))
        require(applied == migrations[:len(applied)], "Database is ahead/divergent; automatic schema downgrade is forbidden.")
        pending = [m for m in migrations if m not in applied]
        if pending:
            require("backend" in components, "Pending migration requires backend publication.")
        plan = dict(sha=sha, branch=branch, components=components, pending=pending, appliedMigrations=applied, previous=known,
                    previousRecommended=self.feed["version"], compatibility=self.feed["compatibility"],
                    composeProject=self.project, privateAccess="local Docker socket on existing private host")
        save(self.op / "plan.json", plan)
        return plan

    def preflight(self, plan):
        self.git("worktree", "add", "--detach", str(self.source), plan["sha"])
        migration_policy(self.source, plan["pending"])
        comparison = self.gh(f"compare/{plan['sha']}...{plan['branch']}")
        require(comparison["status"] in ("ahead", "identical"), "Selected SHA is not on the pushed branch.")
        if "native" in plan["components"]:
            control = self.git("show", "origin/main:.github/workflows/aegis-native-preview.yml")
            require("publish_dev:" in control, "Merge the minimal publish-dev infrastructure PR through protected main first.")
        if "backend" in plan["components"] and plan["previous"]["backend"]["sha"]:
            config_changes = self.git("diff", "--name-only", plan["previous"]["backend"]["sha"], plan["sha"],
                                      "docker-compose.yml", "docker-compose.private.yml")
            require(not config_changes, "Compose changes require reconciliation with the real installation before image publication.")
        checks = self.gh(f"commits/{plan['sha']}/check-runs?per_page=100")
        save(self.op / "existing-checks.json", [{k: c[k] for k in ("name", "status", "conclusion", "head_sha")}
                                               for c in checks["check_runs"]])
        require(not any(c["conclusion"] in ("failure", "cancelled", "timed_out", "action_required")
                        for c in checks["check_runs"] if c["name"] == "Aegis Node CI result"), "Selected commit has a failed required check.")
        # An explicit development publication does not require release acceptance.
        # Pertinent tests below always execute on the selected bytes.
        for c in (self.api, self.pwa, self.pg, *self.others):
            require(c["State"]["Running"], "Existing private service is not running.")
        self.validate(plan, initial=True)

    def prepare(self, plan):
        for component in ("backend", "pwa"):
            if component not in plan["components"]:
                continue
            tag = "aegis-dev-" + component + ":" + plan["sha"]
            if component == "backend":
                context, dockerfile = self.source / "backend", self.source / "backend/Dockerfile"
            else:
                context, dockerfile = self.source, self.source / "frontend/aegis-pwa/Dockerfile"
            self.run(["docker", "build", "--label", REVISION + "=" + plan["sha"], "-t", tag, "-f", dockerfile, context])
            self.images[component] = self.inspect(tag)["Id"]
        if "backend" in plan["components"]:
            sdk = "aegis-dev-tests:" + plan["sha"]
            self.run(["docker", "build", "--target", "build", "-t", sdk, self.source / "backend"])
            self.test_db()
            self.run(["docker", "exec", self.db, "createdb", "-U", "test", "aegis_nodes_test"])
            env = ["-e", "AEGIS_OPERATION_TEST_DATABASE=Host=" + self.db + ";Database=aegis_operation_test;Username=test;Password=disposable_only",
                   "-e", "AEGIS_NODE_TEST_DATABASE=Host=" + self.db + ";Database=aegis_nodes_test;Username=test;Password=disposable_only",
                   "-e", "AEGIS_REMINDER_TEST_DATABASE=Host=" + self.db + ";Database=aegis_operation_test;Username=test;Password=disposable_only"]
            results = self.op / "test-results"
            results.mkdir(mode=0o700)
            self.run(["docker", "run", "--rm", "--network", self.network, "-v", str(results) + ":/tmp/results", *env, sdk,
                      "dotnet", "test", "Aegis.sln", "--logger", "trx", "--results-directory", "/tmp/results"])
            require(list(results.glob("*.trx")), "Backend test evidence is missing.")
            import xml.etree.ElementTree as ET
            summaries = []
            for file in results.glob("*.trx"):
                tree = ET.parse(file)
                counters = tree.find(".//{*}Counters")
                require(counters is not None and int(counters.get("failed", 0)) == 0, "Backend test failures.")
                summaries.append(dict(counters.attrib))
                outcomes = tree.findall(".//{*}UnitTestResult")
                for name in ("PrivateOperationPostgresTests", "NodePostgresTests"):
                    require(any(name in t.get("testName", "") and t.get("outcome") == "Passed" for t in outcomes),
                            "Required disposable contract test didn't run: " + name)
            save(self.op / "backend-test-summary.json", summaries)
        if "pwa" in plan["components"]:
            # Tests execute in a temporary container, never in the personal DB.
            self.run(["docker", "run", "--rm", "-v", str(self.source) + ":/workspace", "-w", "/workspace", "node:24-alpine",
                      "sh", "-ec", "npm ci --prefix packages/aegis-client && npm ci --prefix frontend/aegis-pwa && npm test --prefix frontend/aegis-pwa"])
        self.recovery_proved = not plan["pending"]

    def test_db(self):
        if self.network:
            return
        self.network = "aegis-publish-test-" + self.op.name.lower()
        self.db = self.network + "-pg"
        self.run(["docker", "network", "create", "--internal", self.network])
        self.run(["docker", "run", "-d", "--name", self.db, "--network", self.network,
                  "-e", "POSTGRES_USER=test", "-e", "POSTGRES_PASSWORD=disposable_only",
                  "-e", "POSTGRES_DB=aegis_operation_test", self.pg["Image"]])
        self.disposable.append(self.db)
        for _ in range(60):
            try:
                self.run(["docker", "exec", self.db, "pg_isready", "-U", "test"])
                return
            except Blocked:
                time.sleep(1)
        raise Blocked("Disposable PostgreSQL did not become ready.")

    def admin(self, image, *, rehearsal=False):
        if rehearsal:
            args = ["docker", "run", "--rm", "--network", self.network, "-e",
                    "ConnectionStrings__AegisDatabase=Host=" + self.db + ";Database=recovery;Username=test;Password=disposable_only",
                    "--entrypoint", "dotnet", image, "/app/node-admin/Aegis.NodeAdmin.dll", "migrate"]
        else:
            args = [*self.ndc, "run", "--rm", "--no-deps", "--entrypoint", "dotnet", "aegis-api",
                    "/app/node-admin/Aegis.NodeAdmin.dll", "migrate"]
        self.run(args)

    def inventory(self, *, rehearsal=False, columns=None):
        kw = dict(container=self.db, database="recovery", user="test") if rehearsal else {}
        if columns is None:
            rows = self.sql("SELECT table_name, column_name FROM information_schema.columns WHERE table_schema='public' ORDER BY table_name, ordinal_position;", **kw)
            columns = {}
            for row in rows.splitlines():
                table, column = row.split("|")
                columns.setdefault(table, []).append(column)
        result = {}
        quote = lambda s: '"' + s.replace('"', '""') + '"'
        for table, names in columns.items():
            selected = ",".join(map(quote, names))
            # Stream only hashes/counts from PG; never export personal rows to logs.
            result[table] = self.sql(f"SELECT count(*),md5(COALESCE(string_agg(h,'' ORDER BY h),'')) FROM (SELECT md5(row({selected})::text) h FROM {quote(table)}) s;", **kw)
        return {"columns": columns, "rows": result}

    def backup_and_rehearse(self, plan):
        self.test_db()
        # Stop only the writer. PWA stays available; no DB or vector/graph restart.
        self.writers_stopped = True
        self.run([*self.fdc, "stop", "--timeout", "30", "aegis-api"])
        self.before = self.inventory()
        backup = self.op / "backup"
        backup.mkdir(mode=0o700)
        with (backup / "postgres.dump").open("wb") as output:
            self.run(["docker", "exec", self.pg["Id"], "sh", "-ec",
                      'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc'], output=output)
        with (backup / "keys.tar.gz").open("wb") as output:
            self.run(["docker", "run", "--rm", "--network", "none", "--volumes-from", self.api["Id"] + ":ro",
                      "--entrypoint", "tar", self.api["Image"], "-C", "/root/.aspnet", "-czf", "-", "DataProtection-Keys"], output=output)
        with tarfile.open(backup / "keys.tar.gz") as archive:
            require(any(x.isfile() for x in archive), "Data Protection backup is empty.")
        # Full resolved config (including connection/provider credentials) was
        # saved privately above. Copy mounted FCM credentials without displaying.
        import shutil
        for mount in self.api["Mounts"]:
            if mount["Destination"] == "/run/aegis-fcm":
                shutil.copytree(mount["Source"], backup / "fcm", dirs_exist_ok=True)
        self.run(["docker", "exec", self.db, "createdb", "-U", "test", "recovery"])
        self.run(["docker", "exec", "-i", self.db, "pg_restore", "--exit-on-error", "--no-owner", "--no-privileges", "-U", "test", "-d", "recovery"],
                 data=(backup / "postgres.dump").read_bytes())
        require(self.inventory(rehearsal=True) == self.before, "Backup restore differs from production.")
        self.admin(self.images["backend"], rehearsal=True)
        self.admin(self.images["backend"], rehearsal=True)  # idempotence
        # Old migrator must accept the expanded schema too; never calls Down.
        self.admin(self.api["Image"], rehearsal=True)
        after = self.inventory(rehearsal=True, columns=self.before["columns"])
        require(all(after["rows"][t] == v for t, v in self.before["rows"].items() if t != "__EFMigrationsHistory"),
                "Rehearsal changed historical fields; publication blocked.")
        self.recovery_proved = True
        self.backup_result = dict(restored=True, historicalTables=len(self.before["rows"]) - 1,
                                  sha256=sha256(backup / "postgres.dump"), previousMigratorCompatible=True)
        save(self.op / "backup-result.json", self.backup_result)

    def unchanged(self, plan):
        for original in (self.api, self.pwa, self.pg, *self.others):
            now = self.inspect(original["Name"].lstrip("/"))
            require(now["Id"] == original["Id"] and now["Image"] == original["Image"], "Deployment changed concurrently.")
        # Detect local operator/Compose edits too, before any application writes.
        require(json.loads(self.run([*self.dc, "config", "--format", "json"])) ==
                json.loads((self.op / "compose-before.private.json").read_text()), "Compose changed during publication.")
        require(self.gh("contents/latest.json?ref=aegis-distribution")["sha"] == self.feed_blob,
                "Recommended feed changed concurrently; inspect and rerun.")

    def deploy(self, plan):
        config = json.loads(json.dumps(self.config))
        for component, image in self.images.items():
            service = "aegis-api" if component == "backend" else "aegis-pwa"
            config["services"][service]["image"] = image
            config["services"][service].setdefault("labels", {})[REVISION] = plan["sha"]
        candidate = self.op / "deployment.compose.private.json"
        save(candidate, config)
        self.ndc = ["docker", "compose", "--project-directory", str(self.deploy_root), "-p", self.project, "-f", str(candidate)]
        if plan["pending"]:
            self.migration_attempted = True
            self.admin(self.images["backend"])
            after = self.inventory(columns=self.before["columns"])
            require(all(after["rows"][t] == v for t, v in self.before["rows"].items() if t != "__EFMigrationsHistory"),
                    "Production historical data changed unexpectedly; inspect backup.")
        for component in ("backend", "pwa"):
            if component in self.images:
                self.run([*self.ndc, "up", "-d", "--no-deps", "--no-build", "aegis-api" if component == "backend" else "aegis-pwa"])
        self.writers_stopped = False
        # nginx resolves the upstream at startup; refresh only its resolver state.
        self.run(["docker", "exec", "aegis-pwa", "nginx", "-s", "reload"])
        self.wait_health()

    def http(self, path):
        with urllib.request.urlopen(self.url + path, timeout=10) as response:
            return response.status, response.headers, response.read()

    def wait_health(self):
        for _ in range(45):
            try:
                status, _, body = self.http("/api/health")
                if status == 200 and json.loads(body).get("status") == "ok":
                    return
            except Exception:
                pass
            time.sleep(1)
        raise Blocked("Real private HTTPS API failed health checks.")

    def validate(self, plan, initial=False):
        self.wait_health()
        port = self.api["NetworkSettings"]["Ports"]["8090/tcp"][0]["HostPort"]
        with urllib.request.urlopen("http://127.0.0.1:" + port + "/api/health", timeout=5) as response:
            require(response.status == 200 and json.load(response).get("status") == "ok", "Direct API health failed.")
        for old in (self.pg, *self.others):
            require(self.inspect(old["Name"].lstrip("/"))["Id"] == old["Id"], "Persistent service was recreated.")
        for service, old, component in (("aegis-api", self.api, "backend"), ("aegis-pwa", self.pwa, "pwa")):
            current = self.inspect(service)
            expected = old["Image"] if initial else self.images.get(component, old["Image"])
            require(current["State"]["Running"] and current["Image"] == expected, "Running image differs from selected image.")
            require(current["Mounts"] == old["Mounts"], "Persistent mounts changed.")
            if not initial and component in self.images:
                require(current["Config"]["Labels"].get(REVISION) == plan["sha"], "Running SHA differs from candidate.")
        status, headers, sw = self.http("/sw.js")
        require(status == 200 and "javascript" in headers.get("Content-Type", "") and len(sw) > 100,
                "PWA service worker is unavailable.")
        require("no-cache" in headers.get("Cache-Control", ""), "Service worker cache policy is unsafe.")
        # Read-only normal API contract; no reminder creation or synthetic push.
        for path in ("/api/chat/conversations",):
            status, _, body = self.http(path)
            require(status == 200 and isinstance(json.loads(body), (list, dict)), "Installed client API contract failed.")
        if not initial:
            self.health = {"https": True, "serviceWorker": True, "clientApiV1": True}

    def checkpoint(self, plan):
        state = {"sourceCommit": plan["sha"], "backend": self.identity(self.inspect("aegis-api")),
                 "pwa": self.identity(self.inspect("aegis-pwa")), "recommended": self.feed["version"],
                 "previousRecommended": plan["previousRecommended"], "migrations": plan["pending"],
                 "health": self.health, "compatibility": plan["compatibility"], "evidence": str(self.op)}
        state["migrationHistory"] = plan["appliedMigrations"] + plan["pending"]
        state["backup"] = getattr(self, "backup_result", None)
        save(self.state / "current.json", state)

    def readiness(self, plan, state="success"):
        actual = json.loads((self.state / "current.json").read_text())
        return self.gh(f"statuses/{plan['sha']}", "POST", dict(state=state, context=STATUS,
                       description="api=1;nodeData=1;backend=" + actual["backend"]["sha"] + ";pwa=" + actual["pwa"]["sha"]))

    def native(self, plan):
        # Refuse before version allocation if the minimal trusted control-plane
        # bootstrap hasn't landed. Never run candidate workflows with secrets.
        control = self.git("show", "origin/main:.github/workflows/aegis-native-preview.yml")
        require("publish_dev:" in control, "Merge the minimal publish-dev infrastructure PR through protected main first.")
        ledger = self.gh("contents/ledger.json?ref=aegis-distribution")
        import base64
        reservations = json.loads(base64.b64decode(ledger["content"]))["reservations"]
        line = json.loads((self.source / "apps/aegis-node/package.json").read_text())["version"].split("-")[0]
        events = [int(m[1]) for r in reservations if (m := re.fullmatch(re.escape(line) + r"-unstable\.(\d+)(?:\.rollback\.\d+)?", r["version"]))]
        version = line + "-unstable." + str(max(events, default=0) + 1)
        self.readiness(plan)
        before = {r["id"] for r in self.gh("actions/workflows/aegis-native-preview.yml/runs?event=workflow_dispatch&per_page=100")["workflow_runs"]}
        self.gh("actions/workflows/aegis-native-preview.yml/dispatches", "POST", {"ref": "main", "inputs": {
            "sha": plan["sha"], "branch": plan["branch"], "version": version, "kind": "normal", "platform": "both",
            "compatibility_confirmed": "false", "publish_dev": "true"}})
        run = None
        deadline = time.monotonic() + 7200
        print("Native publication requested: " + version + ". Approve aegis-native-signing once when GitHub requests it.", flush=True)
        while time.monotonic() < deadline:
            self.validate(plan)
            self.readiness(plan)
            if run is None:
                matches = [r for r in self.gh("actions/workflows/aegis-native-preview.yml/runs?event=workflow_dispatch&per_page=100")["workflow_runs"]
                           if r["id"] not in before and version in r["display_title"] and plan["sha"] in r["display_title"]]
                require(len(matches) <= 1, "Ambiguous workflow dispatch.")
                if matches:
                    run = matches[0]["id"]
                    self.native_run = run
                    save(self.op / "native-run.json", {"runId": run, "version": version, "sha": plan["sha"]})
                    print(f"https://github.com/{REPOSITORY}/actions/runs/{run}", flush=True)
            else:
                status = self.gh(f"actions/runs/{run}")
                if status["status"] == "completed":
                    require(status["conclusion"] == "success", "Native signature/publication failed; inspect recorded workflow.")
                    feed = self.gh("contents/latest.json?ref=aegis-distribution")
                    actual = json.loads(base64.b64decode(feed["content"]))
                    require(actual["version"] == version and actual["sourceCommit"] == plan["sha"], "Feed differs from selected publication.")
                    self.native_promoted = True
                    state = json.loads((self.state / "current.json").read_text())
                    state["recommended"] = version
                    save(self.state / "current.json", state)
                    return
            time.sleep(20)
        raise Blocked("Timed out waiting for native distribution; inspect recorded run before retrying.")

    def recover(self, plan):
        require(not self.migration_attempted or self.recovery_proved, "Schema compatibility unproved; automatic image recovery blocked.")
        # After recommending new clients, image recovery needs a fresh contract
        # decision; don't silently put them onto an older incompatible backend.
        require(not self.native_promoted, "Native feed already advanced; inspect before image recovery.")
        self.run([*self.fdc, "up", "-d", "--no-deps", "--no-build", "aegis-api", "aegis-pwa"])
        self.run(["docker", "exec", "aegis-pwa", "nginx", "-s", "reload"])
        self.wait_health()
        self.writers_stopped = False
        if (self.state / "current.json").exists():
            (self.state / "current.json").unlink()

    def on_failure(self, plan):
        if self.native_run and not self.native_promoted:
            try:
                self.readiness(plan, state="failure")
                self.gh(f"actions/runs/{self.native_run}/cancel", "POST")
            except Exception:
                # A workflow that already completed cannot be cancelled. No
                # image recovery follows a successful private checkpoint.
                pass

    def cleanup(self):
        for name in self.disposable:
            self.run(["docker", "rm", "-f", "-v", name])  # only our disposable, unnamed test volume
        if self.network:
            self.run(["docker", "network", "rm", self.network])
        # Preserve pinned source and private evidence for diagnostics/recovery.


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description="Publish selected development code on the existing private Aegis host.")
    parser.add_argument("--ref", required=True, help="pushed branch or full commit SHA")
    parser.add_argument("--full", action="store_true", help="explicitly rebuild all components, allocating a native preview")
    parser.add_argument("--rebuild", action="append", choices=("backend", "pwa"), default=[],
                        help="force a private component rebuild without forcing a native preview")
    parser.add_argument("--plan", action="store_true", help="inspect and print scope without builds/deploy/versions")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    state = Path(os.environ.get("XDG_STATE_HOME", str(Path.home() / ".local/state"))) / "aegis/publish-dev"
    with locked(state):
        port = PrivateHost(root, state)
        plan = port.discover(args.ref, args.full, args.rebuild)
        print(json.dumps(plan, indent=2), flush=True)
        if args.plan:
            return
        journal = publish(port, plan, {})
        journal["deployment"] = json.loads((state / "current.json").read_text())
        port.record(journal)


if __name__ == "__main__":
    try:
        main()
    except (Blocked, KeyboardInterrupt) as error:
        print("publish-dev blocked: " + str(error), file=sys.stderr)
        sys.exit(1)
