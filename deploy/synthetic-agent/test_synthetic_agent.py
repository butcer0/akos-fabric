from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import synthetic_agent


EXAMPLE_MANIFEST = Path(__file__).with_name("example-manifest.json")


class SyntheticAgentTests(unittest.TestCase):
    def test_example_produces_typed_contract_shaped_blocked_result(self) -> None:
        manifest = synthetic_agent.load_manifest(EXAMPLE_MANIFEST)

        result = synthetic_agent.build_result(
            manifest,
            "2026-07-23T12:00:00.000Z",
        )

        self.assertEqual(
            manifest["repositorySessionId"],
            result["repositorySessionId"],
        )
        self.assertEqual("completed", result["status"])
        self.assertEqual(["blocked"], [item["status"] for item in result["items"]])
        usage = result["items"][0]["modelUsage"]
        self.assertEqual(manifest["llm"]["provider"], usage["provider"])
        self.assertEqual(manifest["llm"]["modelId"], usage["modelId"])
        self.assertEqual(0, usage["planner"]["modelCalls"])
        self.assertEqual(0.0, usage["totalEstimatedCostUsd"])

    def test_atomic_writer_leaves_no_temporary_file(self) -> None:
        manifest = synthetic_agent.load_manifest(EXAMPLE_MANIFEST)
        result = synthetic_agent.build_result(
            manifest,
            "2026-07-23T12:00:00.000Z",
        )
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "result.json"

            synthetic_agent.write_json_atomic(destination, result)

            self.assertEqual(
                result,
                json.loads(destination.read_text(encoding="utf-8")),
            )
            self.assertFalse(destination.with_name("result.json.tmp").exists())

    def test_rejects_credentials_embedded_in_clone_url(self) -> None:
        manifest = synthetic_agent.load_manifest(EXAMPLE_MANIFEST)
        manifest["mainRepository"]["cloneUrl"] = (
            "https://token@example.invalid/repository.git"
        )

        with self.assertRaisesRegex(
            synthetic_agent.ManifestError,
            "must not contain credentials",
        ):
            synthetic_agent.build_result(
                manifest,
                "2026-07-23T12:00:00.000Z",
            )


if __name__ == "__main__":
    unittest.main()
