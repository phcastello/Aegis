-- One-time local administration for the fifteen explicitly inventoried Oct03 probes.
-- Provenance: Stage04 seven revoked diagnostics + Stage06 eight; see completion doc.
-- No name-pattern matching. Requires verified private backup and completion migration.
-- psql -v ON_ERROR_STOP=1; transaction rolls back on any changed assumption.
BEGIN;
SELECT pg_advisory_xact_lock(7070003);
SELECT pg_advisory_xact_lock(7070011);
CREATE TEMP TABLE verified_probes(id uuid PRIMARY KEY, created_at timestamptz, revoked_at timestamptz, version text) ON COMMIT DROP;
INSERT INTO verified_probes VALUES
('31ede262-0fb8-421c-baee-d181cc9c41a6'::uuid,'2026-10-03 17:01:59.532121+00'::timestamptz,'2026-10-03 17:01:59.73752+00'::timestamptz,'0.7.0-stage.5'),
('18a3389e-0b35-4d8a-94f7-539e736da585'::uuid,'2026-10-03 17:03:10.696381+00'::timestamptz,'2026-10-03 17:05:41.506454+00'::timestamptz,'0.7.0-stage.5'),
('0d91965a-49ff-48f5-b243-4da61be8361d'::uuid,'2026-10-03 17:03:11.1142+00'::timestamptz,'2026-10-03 17:05:41.533179+00'::timestamptz,'0.7.0-stage.5'),
('aded2a91-9ec4-4281-b6e4-cede184de0ef'::uuid,'2026-10-03 17:16:57.867093+00'::timestamptz,'2026-10-03 17:19:29.058067+00'::timestamptz,'0.7.0-stage.6'),
('0e56b0b4-afd8-480c-96a3-5b89cf35c4ba'::uuid,'2026-10-03 17:16:58.502191+00'::timestamptz,'2026-10-03 17:19:29.102426+00'::timestamptz,'0.7.0-stage.6'),
('bc4949ff-a13b-4105-b6c5-ba0e6a5489a5'::uuid,'2026-10-03 17:38:56.735651+00'::timestamptz,'2026-10-03 17:41:28.164054+00'::timestamptz,'0.7.0-stage.6'),
('6b4e077b-b96d-4eb8-8cd6-d2c60d444d76'::uuid,'2026-10-03 17:38:57.650439+00'::timestamptz,'2026-10-03 17:41:28.209024+00'::timestamptz,'0.7.0-stage.6'),
('b13530e1-e84c-4fd5-97f9-6efb927b4a2f'::uuid,'2026-10-03 21:22:34.26067+00'::timestamptz,'2026-10-03 21:22:51.647779+00'::timestamptz,'0.7.0-stage.8'),
('94ebeffe-c36e-4369-818d-07c41e20c73a'::uuid,'2026-10-03 21:22:35.121279+00'::timestamptz,'2026-10-03 21:22:51.736961+00'::timestamptz,'0.7.0-stage.8'),
('a9533c14-e599-4d24-93fb-27216072603a'::uuid,'2026-10-03 21:24:17.085527+00'::timestamptz,'2026-10-03 21:25:57.633622+00'::timestamptz,'0.7.0-stage.8'),
('469c1698-a7d5-402f-952e-b9ce8c3c9202'::uuid,'2026-10-03 21:24:17.3924+00'::timestamptz,'2026-10-03 21:25:57.66611+00'::timestamptz,'0.7.0-stage.8'),
('dc7b6d86-503a-4bd5-8bf6-9de11acd67c2'::uuid,'2026-10-03 21:27:25.689531+00'::timestamptz,'2026-10-03 21:29:56.488053+00'::timestamptz,'0.7.0-stage.8'),
('7d110387-cefd-49ad-859b-b3eb2223903a'::uuid,'2026-10-03 21:27:25.855576+00'::timestamptz,'2026-10-03 21:29:56.517477+00'::timestamptz,'0.7.0-stage.8'),
('a488202f-91a8-41e9-964e-7be8a5f09b84'::uuid,'2026-10-03 21:31:32.655778+00'::timestamptz,'2026-10-03 21:34:03.28388+00'::timestamptz,'0.7.0-stage.8'),
('fb539131-1784-4433-8b20-5d597c53a229'::uuid,'2026-10-03 21:31:32.952651+00'::timestamptz,'2026-10-03 21:34:03.309769+00'::timestamptz,'0.7.0-stage.8');
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM verified_probes p JOIN aegis_nodes n ON n."Id"=p.id
   LEFT JOIN node_credentials c ON c."NodeId"=n."Id"
   WHERE n."Enabled" OR n."RevokedAt" IS DISTINCT FROM p.revoked_at OR n."CreatedAt"<>p.created_at
     OR n."AppVersion"<>p.version OR c."RevokedAt" IS NULL)
 THEN RAISE EXCEPTION 'Inventory or credential revocation changed; stop for review'; END IF;
 IF EXISTS (SELECT 1 FROM reminder_node_delivery_attempts a JOIN verified_probes p ON p.id=a."NodeId")
 OR EXISTS (SELECT 1 FROM reminder_recipients a JOIN verified_probes p ON p.id=a."NodeId")
 OR EXISTS (SELECT 1 FROM geographic_reminder_rules a JOIN verified_probes p ON p.id=a."ObserverNodeId")
 OR EXISTS (SELECT 1 FROM geographic_reminder_events a JOIN verified_probes p ON p.id=a."ObserverNodeId")
 OR EXISTS (SELECT 1 FROM node_location_profiles a JOIN verified_probes p ON p.id=a."NodeId")
 THEN RAISE EXCEPTION 'Probe acquired user/domain history; stop for review'; END IF;
END $$;
DELETE FROM node_pairing_attempts WHERE "NodeId" IN (SELECT id FROM verified_probes)
 OR "PairingCodeId" IN (SELECT "Id" FROM node_pairing_codes WHERE "IssuerNodeId" IN (SELECT id FROM verified_probes));
DELETE FROM node_pairing_codes WHERE "IssuerNodeId" IN (SELECT id FROM verified_probes);
-- Cascades remove capabilities, revoked credentials and push material. No user history matches.
DELETE FROM aegis_nodes WHERE "Id" IN (SELECT id FROM verified_probes);
COMMIT;
