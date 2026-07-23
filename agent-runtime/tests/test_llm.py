from __future__ import annotations

import unittest

from agent_runtime.llm.gemini import GeminiLlmProvider
from agent_runtime.llm.resolver import (
    LlmProviderResolver,
    UnknownLlmProviderError,
)


class LlmProviderTests(unittest.TestCase):
    def test_resolves_registered_provider_and_rejects_unknown(self) -> None:
        provider = GeminiLlmProvider(llm_factory=lambda **_: object())
        resolver = LlmProviderResolver([provider])

        self.assertIs(provider, resolver.resolve("gemini"))
        with self.assertRaisesRegex(UnknownLlmProviderError, "unknown"):
            resolver.resolve("other")

    def test_rejects_duplicate_provider_registration(self) -> None:
        with self.assertRaisesRegex(ValueError, "duplicate"):
            LlmProviderResolver(
                [
                    GeminiLlmProvider(llm_factory=lambda **_: object()),
                    GeminiLlmProvider(llm_factory=lambda **_: object()),
                ]
            )

    def test_gemini_factory_receives_only_supported_openhands_arguments(self) -> None:
        observed: dict[str, object] = {}

        def factory(**kwargs: object) -> object:
            observed.update(kwargs)
            return "llm"

        provider = GeminiLlmProvider(llm_factory=factory)
        result = provider.create_llm(
            usage_id="planner:item",
            model_id="gemini-3.6-flash",
            openhands_model="gemini/gemini-3.6-flash",
            api_key="not-a-real-key",
        )

        self.assertEqual("llm", result)
        self.assertEqual(
            {
                "usage_id": "planner:item",
                "model": "gemini/gemini-3.6-flash",
                "api_key": "not-a-real-key",
                "num_retries": 1,
            },
            observed,
        )
        self.assertTrue({"temperature", "top_p", "top_k"}.isdisjoint(observed))

    def test_gemini_rejects_inconsistent_model_identifiers(self) -> None:
        provider = GeminiLlmProvider(llm_factory=lambda **_: object())

        with self.assertRaisesRegex(ValueError, "inconsistent"):
            provider.create_llm(
                usage_id="coder:item",
                model_id="gemini-3.6-flash",
                openhands_model="gemini/wrong",
                api_key="not-a-real-key",
            )


if __name__ == "__main__":
    unittest.main()
