import importlib.util
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("publish_dev", Path(__file__).with_name("publish_dev.py"))
dev = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dev)


class FixtureHost:
    """In-memory deployment: no Docker, credentials or personal databases."""
    def __init__(self, failure=None):
        self.failure, self.events = failure, []
        self.writers_stopped = self.backup_valid = self.checkpointed = False
        self.api = self.feed = "old"
        self.legacy = b"legacy-stable-feed"
        self.distributions = {"old": b"immutable-signed-bytes"}
        self.selected = "a" * 40

    def step(self, event):
        self.events.append(event)
        if event == self.failure:
            raise dev.Blocked(event)

    def record(self, journal, **values):
        journal.update(values)

    def pin(self, sha):
        self.step("pin")
        dev.require(sha == self.selected, "Selected commit changed")

    def preflight(self, plan):
        self.step("preflight")
        dev.require(plan.get("safe", True), "Migration uncertain")

    def prepare(self, plan):
        for component in plan["components"]:
            self.step("build-" + component)

    def backup_and_rehearse(self, plan):
        self.writers_stopped = True
        self.step("backup")
        self.step("restore")
        self.backup_valid = True

    def unchanged(self, plan):
        self.step("unchanged")

    def deploy(self, plan):
        if plan["pending"]:
            dev.require(self.backup_valid, "No restored backup")
            self.step("migrate")
        self.api = "candidate"
        self.step("deploy")
        self.writers_stopped = False

    def validate(self, plan):
        self.step("health")

    def checkpoint(self, plan):
        self.checkpointed = True
        self.step("checkpoint")

    def native(self, plan):
        dev.require(self.checkpointed, "API not validated")
        self.step("sign")
        self.step("publish-distribution")
        self.distributions["candidate"] = b"new-immutable-signed-bytes"
        self.step("promote-feed")
        self.feed = "candidate"

    def recover(self, plan):
        self.step("recover-images")
        self.api = "old"
        self.writers_stopped = False

    def on_failure(self, plan):
        self.step("cancel-native-revoke-readiness")

    def cleanup(self):
        self.step("cleanup-disposable")


def plan(components=("backend",), pending=(), **extra):
    return dict(sha="a" * 40, components=list(components), pending=list(pending), **extra)


class PublicationTests(unittest.TestCase):
    def test_backend_only_never_builds_native_or_consumes_version(self):
        host = FixtureHost()
        self.assertTrue(dev.publish(host, plan(), {})["success"])
        self.assertEqual(host.feed, "old")
        self.assertNotIn("sign", host.events)
        self.assertNotIn("build-native", host.events)

    def test_component_dependency_selection(self):
        cases = [(["backend/src/Aegis.Api/A.cs"], {"backend"}),
                 (["frontend/aegis-pwa/src/App.vue"], {"pwa"}),
                 (["docs/a.md", "AGENTS.md", "README.md"], set()),
                 (["apps/aegis-node/README.md", "backend/README.md", "frontend/aegis-pwa/README.md", "packages/aegis-client/README.md"], set()),
                 (["backend/src/Aegis.Infrastructure/Prompts/agent.md"], {"backend"}),
                 (["apps/aegis-node/src-tauri/src/lib.rs"], {"native"}),
                 (["packages/aegis-client/src/App.vue"], {"pwa", "native"}),
                 (["backend/a.cs", "apps/aegis-node/src/App.vue"], {"backend", "native"})]
        for paths, expected in cases:
            with self.subTest(paths=paths):
                self.assertEqual(dev.scope(paths), expected)

    def test_unknown_dependency_blocks(self):
        with self.assertRaisesRegex(dev.Blocked, "Unclassified"):
            dev.scope(["new-runtime/runtime.conf"])

    def test_docs_only_no_application_mutation(self):
        host = FixtureHost()
        dev.publish(host, plan(components=()), {})
        self.assertEqual(host.api, "old")
        self.assertFalse(any(e.startswith("build-") or e == "deploy" for e in host.events))

    def test_pwa_only_no_native_or_backend_build(self):
        host = FixtureHost()
        dev.publish(host, plan(("pwa",)), {})
        self.assertIn("build-pwa", host.events)
        self.assertNotIn("build-backend", host.events)
        self.assertNotIn("sign", host.events)

    def test_native_only_uses_signed_distribution_then_existing_feed(self):
        host = FixtureHost()
        dev.publish(host, plan(("native",)), {})
        self.assertNotIn("deploy", host.events)
        self.assertEqual(host.feed, "candidate")
        self.assertLess(host.events.index("health"), host.events.index("sign"))
        self.assertLess(host.events.index("publish-distribution"), host.events.index("promote-feed"))

    def test_backend_and_native_health_precedes_signing_and_feed(self):
        host = FixtureHost()
        dev.publish(host, plan(("backend", "native")), {})
        self.assertLess(host.events.index("deploy"), host.events.index("health"))
        self.assertLess(host.events.index("checkpoint"), host.events.index("sign"))

    def test_safe_migration_requires_restore_before_production_write(self):
        host = FixtureHost()
        dev.publish(host, plan(pending=["safe"]), {})
        self.assertLess(host.events.index("build-backend"), host.events.index("backup"))
        self.assertLess(host.events.index("restore"), host.events.index("migrate"))

    def test_migration_failure_recovers_images_without_database_downgrade(self):
        host = FixtureHost("migrate")
        with self.assertRaises(dev.Blocked):
            dev.publish(host, plan(pending=["safe"]), {})
        self.assertIn("recover-images", host.events)
        self.assertEqual(host.feed, "old")
        self.assertNotIn("database-downgrade", host.events)

    def test_destructive_or_uncertain_migration_blocks_before_build(self):
        host = FixtureHost()
        with self.assertRaises(dev.Blocked):
            dev.publish(host, plan(pending=["drop"], safe=False), {})
        self.assertNotIn("build-backend", host.events)
        self.assertNotIn("backup", host.events)

    def test_migration_review_binds_exact_bytes_not_name_or_keyword(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            migration = root / "backend/src/Aegis.Infrastructure/Migrations/reviewed.cs"
            migration.parent.mkdir(parents=True)
            migration.write_text("reviewed additive Up")
            with patch.dict(dev.SAFE_MIGRATIONS, {"reviewed": dev.sha256(migration)}):
                dev.migration_policy(root, ["reviewed"])
                migration.write_text("DROP TABLE personal_data")
                with self.assertRaises(dev.Blocked):
                    dev.migration_policy(root, ["reviewed"])
                with self.assertRaises(dev.Blocked):
                    dev.migration_policy(root, ["unreviewed"])

    def test_backup_or_restore_failure_never_migrates_and_restarts_writer(self):
        for failure in ("backup", "restore"):
            with self.subTest(failure=failure):
                host = FixtureHost(failure)
                with self.assertRaises(dev.Blocked):
                    dev.publish(host, plan(pending=["safe"]), {})
                self.assertNotIn("migrate", host.events)
                self.assertFalse(host.writers_stopped)

    def test_build_failure_never_changes_private_installation(self):
        host = FixtureHost("build-backend")
        with self.assertRaises(dev.Blocked):
            dev.publish(host, plan(), {})
        self.assertNotIn("deploy", host.events)
        self.assertNotIn("recover-images", host.events)

    def test_deploy_or_health_failure_recovers_images_never_feed(self):
        for failure in ("deploy", "health"):
            with self.subTest(failure=failure):
                host = FixtureHost(failure)
                result = {}
                with self.assertRaises(dev.Blocked):
                    dev.publish(host, plan(("backend", "native")), result)
                self.assertFalse(result["success"])
                self.assertEqual(host.api, "old")
                self.assertEqual(host.feed, "old")
                self.assertNotIn("sign", host.events)

    def test_sign_distribution_or_feed_failure_retains_healthy_backend_and_legacy(self):
        for failure in ("sign", "publish-distribution", "promote-feed"):
            with self.subTest(failure=failure):
                host = FixtureHost(failure)
                result = {}
                with self.assertRaises(dev.Blocked):
                    dev.publish(host, plan(("backend", "native")), result)
                self.assertFalse(result["success"])
                self.assertEqual(host.feed, "old")
                self.assertEqual(host.api, "candidate")
                self.assertEqual(host.legacy, b"legacy-stable-feed")
                self.assertEqual(host.distributions["old"], b"immutable-signed-bytes")

    def test_retry_partial_failure_native_without_redeploying_api(self):
        host = FixtureHost("sign")
        with self.assertRaises(dev.Blocked):
            dev.publish(host, plan(("backend", "native")), {})
        self.assertTrue(host.checkpointed)
        host.failure = None
        host.events.clear()
        self.assertTrue(dev.publish(host, plan(("native",)), {})["success"])
        self.assertNotIn("deploy", host.events)

    def test_reexecution_no_new_distribution(self):
        host = FixtureHost()
        dev.publish(host, plan(), {})
        host.events.clear()
        dev.publish(host, plan(components=()), {})
        self.assertNotIn("deploy", host.events)
        self.assertNotIn("sign", host.events)

    def test_different_commit_and_concurrent_deployment_block(self):
        for failure in ("wrong-sha", "unchanged"):
            host = FixtureHost("unchanged" if failure == "unchanged" else None)
            candidate = plan()
            if failure == "wrong-sha":
                candidate["sha"] = "b" * 40
            with self.assertRaises(dev.Blocked):
                dev.publish(host, candidate, {})
            self.assertNotIn("deploy", host.events)

    def test_concurrent_invocations_excluded_lock_released(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory)
            with dev.locked(state):
                with self.assertRaisesRegex(dev.Blocked, "Another"):
                    with dev.locked(state):
                        self.fail("Second invocation acquired lock")
            with dev.locked(state):
                pass

    def test_cleanup_failure_is_reported_instead_of_false_success(self):
        host = FixtureHost("cleanup-disposable")
        result = {}
        with self.assertRaises(dev.Blocked):
            dev.publish(host, plan(), result)
        self.assertFalse(result["success"])
        self.assertIn("cleanupError", result)

    def test_disposable_database_waits_for_final_tcp_server_and_never_publishes_ports(self):
        host = object.__new__(dev.PrivateHost)
        host.network, host.disposable = None, []
        host.op = Path("fixture-only")
        host.pg = {"Image": "postgres-fixture"}
        commands = []
        host.run = lambda args, **kw: commands.append(args) or ""
        host.test_db()
        readiness = commands[-1]
        self.assertIn("pg_isready", readiness)
        self.assertEqual(readiness[readiness.index("-h") + 1], "127.0.0.1")
        start = next(c for c in commands if c[:3] == ["docker", "run", "-d"])
        self.assertIn("--tmpfs", start)
        self.assertNotIn("-p", start)
        self.assertNotIn("--publish", start)

    def test_worker_health_observes_real_polling_without_creating_reminders(self):
        with tempfile.TemporaryDirectory() as directory:
            host = object.__new__(dev.PrivateHost)
            host.source = host.op = Path(directory)
            registration = host.source / "backend/src/Aegis.Infrastructure/DependencyInjection.cs"
            registration.parent.mkdir(parents=True)
            registration.write_text("services.AddHostedService<A.ReminderInterventionWorker>();\nservices.AddHostedService<A.ProactiveNotificationWorker>();")
            queries = []
            answers = iter(['{"reminder_interventions":10,"proactive_notifications":20}',
                            '{"reminder_interventions":11,"proactive_notifications":21}'])
            host.sql = lambda query: queries.append(query) or next(answers)
            with patch.object(dev.time, "sleep"):
                host.verify_proactivity_workers()
            self.assertTrue(host.worker_evidence["ordinaryDatabasePollingObserved"])
            self.assertTrue(all(q.strip().startswith("SELECT") for q in queries))

    def test_actual_git_checkout_rejects_dirty_source_and_different_commit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.check_output(["git", "-C", str(root), *args], stderr=subprocess.DEVNULL).decode().strip()
            git("init")
            git("config", "user.email", "fixture@example.invalid")
            git("config", "user.name", "Fixture")
            (root / "source.txt").write_text("selected")
            git("add", "source.txt")
            git("commit", "-m", "fixture")
            sha = git("rev-parse", "HEAD")
            state = root / "state"
            state.mkdir()
            host = dev.PrivateHost(root, state)
            host.git("worktree", "add", "--detach", str(host.source), sha)
            host.pin(sha)
            (host.source / "source.txt").write_text("changed after selection")
            with self.assertRaisesRegex(dev.Blocked, "modified"):
                host.pin(sha)
            with self.assertRaises(dev.Blocked):
                host.pin("b" * 40)

    def test_docker_adapter_updates_only_the_affected_private_service(self):
        for component, service in (("backend", "aegis-api"), ("pwa", "aegis-pwa")):
            with self.subTest(component=component), tempfile.TemporaryDirectory() as directory:
                host = object.__new__(dev.PrivateHost)
                host.op = host.deploy_root = Path(directory)
                host.project = "fixture"
                host.config = {"services": {"aegis-api": {"image": "old-api"}, "aegis-pwa": {"image": "old-pwa"}}}
                host.images = {component: "new-image"}
                commands = []
                host.run = lambda args, **kw: commands.append(args) or ""
                host.wait_health = lambda: None
                host.deploy(plan((component,)))
                ups = [c for c in commands if "up" in c]
                self.assertEqual(len(ups), 1)
                self.assertEqual(ups[0][-1], service)
                self.assertIn("--no-deps", ups[0])
                self.assertIn("--no-build", ups[0])
                import json
                config = json.loads((host.op / "deployment.compose.private.json").read_text())
                other = "aegis-pwa" if service == "aegis-api" else "aegis-api"
                self.assertEqual(config["services"][other], host.config["services"][other])


if __name__ == "__main__":
    unittest.main()
