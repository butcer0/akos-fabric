"""Metadata-only OpenTelemetry instrumentation for the repository runtime.

The operational image installs the pinned OpenTelemetry API through the pinned
OpenHands dependency graph.  Unit-test and contract-only installations may omit
that optional graph; instrumentation then degrades to a no-op without changing
runtime behavior.
"""

from __future__ import annotations

import os
import time
from contextlib import contextmanager
from dataclasses import dataclass
from typing import Any, Iterator, Mapping

try:
    from opentelemetry import metrics, trace
    from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import (
        OTLPMetricExporter,
    )
    from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import (
        OTLPSpanExporter,
    )
    from opentelemetry.sdk.metrics import MeterProvider
    from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
    from opentelemetry.sdk.resources import Resource
    from opentelemetry.sdk.trace import TracerProvider
    from opentelemetry.sdk.trace.export import BatchSpanProcessor
    from opentelemetry.trace.propagation.tracecontext import (
        TraceContextTextMapPropagator,
    )
except ImportError:  # pragma: no cover - exercised by the dependency-light suite
    metrics = None
    trace = None
    MeterProvider = None
    OTLPMetricExporter = None
    OTLPSpanExporter = None
    PeriodicExportingMetricReader = None
    Resource = None
    TracerProvider = None
    BatchSpanProcessor = None
    TraceContextTextMapPropagator = None


_INSTRUMENTATION_NAME = "AkosFabric.AgentRuntime"
_INSTRUMENTATION_VERSION = "1.4"
_SERVICE_NAME = "akos-fabric-repository-agent"
_trace_provider: Any | None = None
_meter_provider: Any | None = None


def configure_runtime_telemetry() -> None:
    """Configure one OTLP pipeline when the host supplied an endpoint."""

    global _meter_provider, _trace_provider

    if (
        not os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT")
        or trace is None
        or metrics is None
        or Resource is None
        or TracerProvider is None
        or BatchSpanProcessor is None
        or OTLPSpanExporter is None
        or MeterProvider is None
        or PeriodicExportingMetricReader is None
        or OTLPMetricExporter is None
    ):
        return
    if _trace_provider is not None or _meter_provider is not None:
        return

    trace_provider = None
    meter_provider = None
    try:
        resource = Resource.create(
            {
                "service.name": _SERVICE_NAME,
                "service.version": _INSTRUMENTATION_VERSION,
            }
        )
        trace_provider = TracerProvider(resource=resource)
        trace_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
        meter_provider = MeterProvider(
            resource=resource,
            metric_readers=[
                PeriodicExportingMetricReader(OTLPMetricExporter()),
            ],
        )
        trace.set_tracer_provider(trace_provider)
        metrics.set_meter_provider(meter_provider)
        _trace_provider = trace_provider
        _meter_provider = meter_provider
    except Exception:
        # Telemetry backend/configuration failure is explicitly non-fatal.
        if meter_provider is not None:
            meter_provider.shutdown()
        if trace_provider is not None:
            trace_provider.shutdown()


def shutdown_runtime_telemetry() -> None:
    """Flush bounded metadata-only telemetry before the container exits."""

    global _meter_provider, _trace_provider

    if _meter_provider is not None:
        try:
            _meter_provider.shutdown()
        except Exception:
            pass
        _meter_provider = None
    if _trace_provider is not None:
        try:
            _trace_provider.shutdown()
        except Exception:
            pass
        _trace_provider = None


class _EnvironmentGetter:
    def get(self, carrier: Mapping[str, str], key: str) -> list[str] | None:
        value = carrier.get(key.upper())
        return [value] if value else None

    def keys(self, carrier: Mapping[str, str]) -> list[str]:
        return [key.lower() for key in carrier]


@dataclass
class RuntimeSpan:
    """Small safe facade that never exposes span implementation details."""

    _span: Any | None

    def set(self, **attributes: str | int | float | bool) -> None:
        self.set_attributes(attributes)

    def set_attributes(
        self, attributes: Mapping[str, str | int | float | bool]
    ) -> None:
        if self._span is not None:
            self._span.set_attributes(attributes)


class RuntimeTelemetry:
    """Creates only low-cardinality instruments and metadata-only spans."""

    def __init__(self) -> None:
        self._tracer = (
            trace.get_tracer(_INSTRUMENTATION_NAME, _INSTRUMENTATION_VERSION)
            if trace is not None
            else None
        )
        meter = (
            metrics.get_meter(_INSTRUMENTATION_NAME, _INSTRUMENTATION_VERSION)
            if metrics is not None
            else None
        )
        self._sessions = (
            meter.create_counter("akos_repository_sessions_total")
            if meter is not None
            else None
        )
        self._session_duration = (
            meter.create_histogram(
                "akos_repository_session_duration_seconds", unit="s"
            )
            if meter is not None
            else None
        )
        self._items = (
            meter.create_counter("akos_work_items_total")
            if meter is not None
            else None
        )
        self._item_duration = (
            meter.create_histogram("akos_work_item_duration_seconds", unit="s")
            if meter is not None
            else None
        )
        self._model_requests = (
            meter.create_counter("akos_model_requests_total")
            if meter is not None
            else None
        )
        self._input_tokens = (
            meter.create_counter("akos_model_input_tokens_total")
            if meter is not None
            else None
        )
        self._output_tokens = (
            meter.create_counter("akos_model_output_tokens_total")
            if meter is not None
            else None
        )
        self._model_cost = (
            meter.create_counter("akos_model_cost_usd_total", unit="USD")
            if meter is not None
            else None
        )
        self._verification_failures = (
            meter.create_counter("akos_verification_failures_total")
            if meter is not None
            else None
        )
        self._judge_dispositions = (
            meter.create_counter("akos_judge_dispositions_total")
            if meter is not None
            else None
        )
        # Change-request creation happens in Agent Control, not this container.
        # Creating the same instrument name here would produce a misleading zero
        # series, so that system metric remains owned by the control plane.

    @contextmanager
    def span(
        self,
        name: str,
        attributes: Mapping[str, str | int | float | bool] | None = None,
        *,
        remote_parent_from_environment: bool = False,
    ) -> Iterator[RuntimeSpan]:
        if self._tracer is None:
            yield RuntimeSpan(None)
            return

        context = None
        if (
            remote_parent_from_environment
            and TraceContextTextMapPropagator is not None
        ):
            context = TraceContextTextMapPropagator().extract(
                os.environ, getter=_EnvironmentGetter()
            )
        started = time.monotonic()
        with self._tracer.start_as_current_span(
            name, context=context, attributes=dict(attributes or {})
        ) as span:
            facade = RuntimeSpan(span)
            try:
                yield facade
            except BaseException as error:
                facade.set(
                    success=False,
                    failure_type=type(error).__name__,
                    duration_ms=int((time.monotonic() - started) * 1000),
                )
                raise
            else:
                facade.set(
                    success=True,
                    duration_ms=int((time.monotonic() - started) * 1000),
                )

    def record_session(self, status: str, duration_seconds: float) -> None:
        attributes = {"status": status}
        if self._sessions is not None:
            self._sessions.add(1, attributes)
        if self._session_duration is not None:
            self._session_duration.record(duration_seconds, attributes)

    def record_item(self, status: str, duration_seconds: float) -> None:
        attributes = {"status": status}
        if self._items is not None:
            self._items.add(1, attributes)
        if self._item_duration is not None:
            self._item_duration.record(duration_seconds, attributes)

    def record_model_usage(
        self,
        *,
        provider: str,
        model: str,
        role: str,
        requests: int,
        input_tokens: int,
        output_tokens: int,
        cost_usd: float,
    ) -> None:
        attributes = {"provider": provider, "model": model, "role": role}
        if self._model_requests is not None:
            self._model_requests.add(requests, attributes)
        if self._input_tokens is not None:
            self._input_tokens.add(input_tokens, attributes)
        if self._output_tokens is not None:
            self._output_tokens.add(output_tokens, attributes)
        if self._model_cost is not None:
            self._model_cost.add(cost_usd, attributes)

    def record_verification_failure(self, command_name: str) -> None:
        if self._verification_failures is not None:
            self._verification_failures.add(1, {"command": command_name})

    def record_judge_disposition(self, disposition: str) -> None:
        if self._judge_dispositions is not None:
            self._judge_dispositions.add(1, {"disposition": disposition})

configure_runtime_telemetry()
runtime_telemetry = RuntimeTelemetry()
