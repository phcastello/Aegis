"""Opt-in recovery rehearsal with synthetic data and already-built local images.

AEGIS_PUBLISH_TEST_OLD_IMAGE=sha256:... AEGIS_PUBLISH_TEST_NEW_IMAGE=sha256:... \
  python3 -m unittest discover -s scripts -p publish_dev_docker_test.py
Never discovers or connects to a personal deployment; no production names/ports.
"""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("publish_dev", Path(__file__).with_name("publish_dev.py"))
dev = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dev)


@unittest.skipUnless(os.environ.get("AEGIS_PUBLISH_TEST_OLD_IMAGE") and os.environ.get("AEGIS_PUBLISH_TEST_NEW_IMAGE"),
                     "Requires immutable old/new local fixture images; never uses the personal installation.")
class DockerRecoveryTests(unittest.TestCase):
    def test_backup_restore_migration_idempotence_and_old_api_recovery_with_synthetic_data(self):
        os.umask(0o077)
        old, new = (os.environ[k] for k in ("AEGIS_PUBLISH_TEST_OLD_IMAGE", "AEGIS_PUBLISH_TEST_NEW_IMAGE"))
        self.assertRegex(old, r"^sha256:[a-f0-9]{64}$")
        self.assertRegex(new, r"^sha256:[a-f0-9]{64}$")
        with tempfile.TemporaryDirectory() as directory:
            host = dev.PrivateHost(Path(__file__).resolve().parents[1], Path(directory))
            host.pg = {"Image": host.inspect("postgres:16-alpine")["Id"]}
            try:
                host.test_db()
                host.pg = host.inspect(host.db)
                host.pg_env = dict(x.split("=", 1) for x in host.pg["Config"]["Env"])
                host.images = {"backend": new}
                host.run(["docker", "run", "--rm", "--network", host.network, "-e",
                          "ConnectionStrings__AegisDatabase=Host=" + host.db + ";Database=aegis_operation_test;Username=test;Password=disposable_only",
                          "--entrypoint", "dotnet", old, "/app/node-admin/Aegis.NodeAdmin.dll", "migrate"])
                host.sql('''INSERT INTO conversations ("Id","Title","CreatedAt","UpdatedAt")
                            VALUES ('00000000-0000-4000-8000-000000000001','synthetic backup fixture',now(),now());
                            INSERT INTO chat_messages ("Id","ConversationId","Role","Content","CreatedAt","UpdatedAt")
                            VALUES ('00000000-0000-4000-8000-000000000002','00000000-0000-4000-8000-000000000001','user','synthetic only',now(),now());''')
                writer = host.network + "-fixture-writer"
                keys = host.op / "fixture-keys"
                keys.mkdir()
                host.run(["docker", "run", "-d", "--name", writer, "--network", "none",
                          "-v", str(keys) + ":/root/.aspnet/DataProtection-Keys", "--entrypoint", "sleep", old, "infinity"])
                host.disposable.append(writer)
                host.run(["docker", "exec", "-i", writer, "sh", "-c",
                          "cat > /root/.aspnet/DataProtection-Keys/fixture.xml"], data=b"<fixture>synthetic-only</fixture>")
                host.api = host.inspect(writer)
                host.fdc = ["fixture-compose"]
                original_run = host.run

                def fixture_compose(args, **kwargs):
                    if args[0] == "fixture-compose":
                        self.assertEqual(args[1:], ["stop", "--timeout", "30", "aegis-api"])
                        args = ["docker", "stop", "--time", "1", writer]
                    return original_run(args, **kwargs)

                with patch.object(host, "run", side_effect=fixture_compose):
                    host.backup_and_rehearse({"pending": ["20261022133709_ReminderInterventions"]})
                self.assertTrue(host.recovery_proved)
                self.assertTrue(host.backup_result["restored"])
                self.assertTrue(host.backup_result["previousApiReadContract"])
                # Preparing recovery must not migrate even the synthetic source DB.
                self.assertEqual(host.sql("SELECT to_regclass('public.reminder_interventions');"), "")
                self.assertEqual(host.sql('SELECT "Content" FROM chat_messages;'), "synthetic only")
                self.assertGreater((host.op / "backup/postgres.dump").stat().st_size, 0)
                print(json.dumps(host.backup_result))
            except Exception:
                print((host.op / "operations.private.log").read_text(errors="replace")[-5000:])
                raise
            finally:
                host.cleanup()


if __name__ == "__main__":
    unittest.main()
