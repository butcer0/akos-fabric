from __future__ import annotations

import json
import unittest
from copy import deepcopy

from pydantic import ValidationError

from agent_runtime.manifest import SessionManifest
from tests.support import make_manifest


class SessionManifestTests(unittest.TestCase):
    def test_round_trip_uses_wire_aliases_and_contains_no_secret(self) -> None:
        manifest = make_manifest()

        wire = manifest.model_dump(mode="json", by_alias=True)

        self.assertEqual("openHandsModel", next(
            key for key in wire["llm"] if key == "openHandsModel"
        ))
        self.assertNotIn("apiKey", json.dumps(wire))
        reparsed = SessionManifest.model_validate_json(json.dumps(wire))
        self.assertEqual(manifest, reparsed)

    def test_accepts_strict_supplemental_repository_contract(self) -> None:
        payload = make_manifest().model_dump(mode="json", by_alias=True)
        payload["supplementalRepositories"] = [
            {
                "providerRepositoryId": "butcer0/shared-library",
                "cloneUrl": "https://github.com/butcer0/shared-library.git",
                "defaultBranch": "main",
                "cloneStrategy": "full",
                "gitLfs": False,
                "submodules": "none",
                "writable": False,
            }
        ]

        parsed = SessionManifest.model_validate_json(json.dumps(payload))

        self.assertEqual(1, len(parsed.supplemental_repositories))
        self.assertFalse(parsed.supplemental_repositories[0].writable)

    def test_rejects_duplicate_repository_identifiers(self) -> None:
        payload = make_manifest().model_dump(mode="json", by_alias=True)
        payload["supplementalRepositories"] = [
            {
                **payload["mainRepository"],
                "writable": False,
            }
        ]

        with self.assertRaisesRegex(ValidationError, "must be unique"):
            SessionManifest.model_validate_json(json.dumps(payload))

    def test_rejects_items_beyond_manifest_limit(self) -> None:
        payload = make_manifest().model_dump(mode="json", by_alias=True)
        second = deepcopy(payload["workItems"][0])
        second["workItemRunId"] = "11111111-1111-4111-8111-111111111111"
        second["sequenceNumber"] = 2
        second["jiraKey"] = "KAN-2"
        payload["workItems"].append(second)
        payload["limits"]["maximumItems"] = 1

        with self.assertRaisesRegex(ValidationError, "maximumItems"):
            SessionManifest.model_validate_json(json.dumps(payload))

    def test_rejects_duplicate_and_unordered_work_items(self) -> None:
        payload = make_manifest().model_dump(mode="json", by_alias=True)
        second = deepcopy(payload["workItems"][0])
        second["workItemRunId"] = "11111111-1111-4111-8111-111111111111"
        second["jiraKey"] = "KAN-2"
        second["sequenceNumber"] = 0
        payload["workItems"].append(second)

        with self.assertRaises(ValidationError):
            SessionManifest.model_validate_json(json.dumps(payload))

    def test_rejects_naive_jira_timestamp(self) -> None:
        payload = make_manifest().model_dump(mode="json", by_alias=True)
        payload["workItems"][0]["jiraUpdatedAt"] = "2026-07-23T11:45:02"

        with self.assertRaisesRegex(ValidationError, "UTC offset"):
            SessionManifest.model_validate_json(json.dumps(payload))

    def test_rejects_short_or_uppercase_git_sha(self) -> None:
        for invalid_sha in ("abc123", "A" * 40):
            with self.subTest(invalid_sha=invalid_sha):
                payload = make_manifest().model_dump(mode="json", by_alias=True)
                payload["profileRevisionSha"] = invalid_sha

                with self.assertRaises(ValidationError):
                    SessionManifest.model_validate_json(json.dumps(payload))

    def test_rejects_uppercase_image_digest(self) -> None:
        payload = make_manifest().model_dump(mode="json", by_alias=True)
        payload["imageDigest"] = "sha256:" + ("A" * 64)

        with self.assertRaises(ValidationError):
            SessionManifest.model_validate_json(json.dumps(payload))

    def test_rejects_invalid_jira_key(self) -> None:
        payload = make_manifest().model_dump(mode="json", by_alias=True)
        payload["workItems"][0]["jiraKey"] = "kan-01"

        with self.assertRaises(ValidationError):
            SessionManifest.model_validate_json(json.dumps(payload))


if __name__ == "__main__":
    unittest.main()
