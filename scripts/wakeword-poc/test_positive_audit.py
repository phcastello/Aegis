#!/usr/bin/env python3
"""Integrity tests for the merged inventory boundary, without private recordings."""
import csv
import hashlib
import io
import unittest
import zipfile

from positive_audit import check_sums, check_csv, temporal_checks


class MergedAuditTests(unittest.TestCase):
    def archive(self, bodies, sums):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            for name, body in bodies.items():
                archive.writestr(name, body)
            archive.writestr('SHA256SUMS.txt', sums)
        stream.seek(0)
        return zipfile.ZipFile(stream)

    def test_member_hashes_cover_entire_archive(self):
        body = b'original bytes'
        sums = hashlib.sha256(body).hexdigest()+'  dataset.jsonl\n'
        with self.archive({'dataset.jsonl': body}, sums) as archive:
            self.assertEqual(set(check_sums(archive)), {'dataset.jsonl'})
        with self.archive({'dataset.jsonl': body, 'extra.wav': b'extra'}, sums) as archive:
            with self.assertRaises(ValueError):
                check_sums(archive)

    def test_changed_bytes_and_repeated_hash_entry_rejected(self):
        sums = hashlib.sha256(b'original').hexdigest()+'  dataset.jsonl\n'
        with self.archive({'dataset.jsonl': b'changed'}, sums) as archive:
            with self.assertRaises(ValueError):
                check_sums(archive)
        with self.archive({'dataset.jsonl': b'original'}, sums+sums) as archive:
            with self.assertRaises(ValueError):
                check_sums(archive)

    def row(self):
        row = {k: '' for k in ('id', 'humanLabel', 'labeledAt', 'activatedAt', 'endedAt',
               'groupId', 'groupUnknown', 'modelVersion', 'modelSha256', 'appVersion', 'build',
               'datasetGeneratedAt')}
        row.update(id='synthetic-test-id', humanLabel='correct', groupUnknown=False, endedAt=None,
                   activatedAt='2026-11-05T12:00:00-03:00', exportedAt='2026-11-05T12:01:00-03:00',
                   labeledAt='2026-11-05T16:02:00Z', datasetGeneratedAt='2026-11-05T16:03:00Z')
        row['provenance'] = {'sourceExportedAt': row['exportedAt'], 'originalEventId': row['id']}
        for kind in ('triggerContext', 'postActivation'):
            row[kind] = {'path': kind+'.wav', 'sha256': 'a'*64}
        return row

    def csv(self, row):
        from positive_audit import scalar
        record = {k:scalar(row[k]) for k in ('id', 'humanLabel', 'labeledAt', 'activatedAt', 'endedAt',
               'groupId', 'groupUnknown', 'modelVersion', 'modelSha256', 'appVersion', 'build',
               'datasetGeneratedAt')}
        record['sourceExportedAt'] = row['exportedAt']
        for kind in ('triggerContext', 'postActivation'):
            record[kind+'Path'] = row[kind]['path']
            record[kind+'Sha256'] = row[kind]['sha256']
        stream = io.StringIO()
        writer = csv.DictWriter(stream, fieldnames=record)
        writer.writeheader(); writer.writerow(record)
        return stream.getvalue().encode()

    def test_csv_null_boolean_and_every_field_match(self):
        row = self.row()
        check_csv([row], self.csv(row))
        changed = dict(row, build=999)
        with self.assertRaises(ValueError):
            check_csv([row], self.csv(changed))

    def test_interrupted_end_does_not_invalidate_intact_context(self):
        self.assertIsNone(temporal_checks(self.row()))

    def test_time_order_and_timezone_checked(self):
        row = self.row()
        row['endedAt'] = '2026-11-05T11:00:00-03:00'
        with self.assertRaises(ValueError):
            temporal_checks(row)
        row = self.row()
        row['activatedAt'] = '2026-11-05T12:00:00'
        with self.assertRaises(ValueError):
            temporal_checks(row)


if __name__ == '__main__':
    unittest.main()
