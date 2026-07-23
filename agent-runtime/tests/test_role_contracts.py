from __future__ import annotations

import unittest

from pydantic import ValidationError

from agent_runtime.contracts.candidate import CriterionEvidence
from agent_runtime.contracts.judgment import JudgmentV1
from agent_runtime.contracts.plan import PlanV1, PlannedFile
from tests.support import CANDIDATE_SHA


class RoleContractTests(unittest.TestCase):
    def test_mutable_defaults_are_not_shared(self) -> None:
        first = PlannedFile(path="one.py", purpose="first")
        second = PlannedFile(path="two.py", purpose="second")

        first.symbols.append("symbol")

        self.assertEqual(["symbol"], first.symbols)
        self.assertEqual([], second.symbols)

    def test_plan_confidence_is_bounded(self) -> None:
        payload = {
            "objective": "Do the work",
            "source_findings": [],
            "assumptions": [],
            "files": [],
            "implementation_steps": [],
            "tests_to_add_or_change": [],
            "verification": [],
            "risks": [],
            "blockers": [],
            "confidence": 1.1,
        }

        with self.assertRaises(ValidationError):
            PlanV1.model_validate(payload)

    def test_judge_cannot_accept_failed_deterministic_verification(self) -> None:
        with self.assertRaisesRegex(
            ValidationError, "deterministic verification failed"
        ):
            JudgmentV1(
                candidate_sha=CANDIDATE_SHA,
                deterministic_verification_passed=False,
                acceptance_criteria_satisfied=True,
                findings=[],
                disposition="accept",
                summary="Looks good",
            )

    def test_contracts_reject_unknown_fields(self) -> None:
        with self.assertRaises(ValidationError):
            CriterionEvidence(
                criterion="It works",
                evidence="A test proves it",
                paths=[],
                invented=True,
            )


if __name__ == "__main__":
    unittest.main()
