import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import perf_sidecar_evaluate as ev  # noqa: E402

ACTIVE = {"requestsOk": 5, "requestsFailed": 0}
IDLE = {"requestsOk": 0, "requestsFailed": 0}


def alloc(total, probed=("malloc", "calloc")):
    return {"data": {"totalSampledAllocations": total, "probedFunctions": list(probed)}}


class EvaluateTests(unittest.TestCase):
    def test_timeout_wins(self):
        self.assertEqual(ev.evaluate("cpu", None, 124, True, ACTIVE)["status"], "timeout")

    def test_exit_zero_with_empty_observations_is_not_passed(self):
        self.assertEqual(ev.evaluate("native-alloc", alloc(0), 0, False, ACTIVE)["status"], "failed")

    def test_idle_target_is_not_passed(self):
        self.assertEqual(ev.evaluate("native-alloc", alloc(10), 0, False, IDLE)["status"], "failed")

    def test_alloc_passes_with_observations_and_probed_symbol(self):
        result = ev.evaluate("native-alloc", alloc(10), 0, False, ACTIVE)
        self.assertEqual(result["status"], "passed")
        self.assertEqual(result["observations"], 10)

    def test_alloc_requires_malloc_probe(self):
        self.assertEqual(ev.evaluate("native-alloc", alloc(10, ("calloc",)), 0, False, ACTIVE)["status"], "failed")

    def test_cpu_requires_perf_provenance_and_volume(self):
        payload = {"summary": "Evidence: LinuxPerf/OsOnCpuSamples", "data": {"totalSamples": 50, "evidence": {}}}
        self.assertEqual(ev.evaluate("cpu", payload, 0, False, ACTIVE)["status"], "passed")
        payload["data"]["totalSamples"] = 1
        self.assertEqual(ev.evaluate("cpu", payload, 0, False, ACTIVE)["status"], "failed")
        payload = {"summary": "EventPipe", "data": {"totalSamples": 50, "evidence": {}}}
        self.assertEqual(ev.evaluate("cpu", payload, 0, False, ACTIVE)["status"], "failed")

    def test_lock_contention_requires_mutex_probe(self):
        payload = {"data": {"totalSampledLockCalls": 3, "probedFunctions": ["pthread_mutex_lock"]}}
        self.assertEqual(ev.evaluate("native-lock-contention", payload, 0, False, ACTIVE)["status"], "passed")

    def test_off_cpu_needs_observations(self):
        self.assertEqual(ev.evaluate("off_cpu", {"data": {"totalSamples": 0}}, 0, False, ACTIVE)["status"], "failed")
        self.assertEqual(ev.evaluate("off_cpu", {"data": {"totalSamples": 7}}, 0, False, ACTIVE)["status"], "passed")

    def test_classified_prerequisite_error_is_unsupported(self):
        payload = {"error": {"kind": "PermissionDenied", "message": "no CAP_PERFMON"}}
        self.assertEqual(ev.evaluate("off_cpu", payload, 1, False, ACTIVE)["status"], "unsupported")

    def test_unclassified_error_is_failed(self):
        payload = {"error": {"kind": "Internal", "message": "boom"}}
        self.assertEqual(ev.evaluate("off_cpu", payload, 1, False, ACTIVE)["status"], "failed")

    def test_missing_json_is_failed(self):
        self.assertEqual(ev.evaluate("cpu", None, 1, False, ACTIVE)["status"], "failed")

    def test_negative_control_requires_expected_kind_and_actionable_message(self):
        payload = {"error": {"kind": "PermissionDenied", "message": "No permission to enable cycles; grant CAP_PERFMON"}}
        passed = ev.evaluate("cpu", payload, 1, False, IDLE, "PermissionDenied", "permission")
        self.assertEqual(passed["status"], "passed")
        wrong_kind = ev.evaluate("cpu", payload, 1, False, IDLE, "UnsupportedPrerequisite")
        self.assertEqual(wrong_kind["status"], "failed")
        vague = {"error": {"kind": "PermissionDenied", "message": "failed"}}
        self.assertEqual(ev.evaluate("cpu", vague, 1, False, IDLE, "PermissionDenied", "permission")["status"], "failed")
        self.assertEqual(ev.evaluate("cpu", {"data": {}}, 0, False, IDLE, "PermissionDenied")["status"], "failed")

    def test_activation_summary_counts_status_codes(self):
        path = Path(self.id() + ".log")
        try:
            path.write_text("200 0.1\n200 0.1\n000 0\n", encoding="utf-8")
            self.assertEqual(ev.activation_summary(path), {"requestsOk": 2, "requestsFailed": 1})
        finally:
            path.unlink(missing_ok=True)

    def test_capability_decoding_and_topology(self):
        self.assertEqual(ev.decode_capabilities("0000004000080000"), ["SYS_PTRACE", "PERFMON"])
        path = Path(self.id() + ".raw")
        try:
            path.write_text("sidecar.CapEff=0000004000080000\nsidecar.uid=0\nhost.kernel=6.8\n", encoding="utf-8")
            topology = ev.build_topology(path)
            self.assertEqual(topology["sidecar"]["effectiveCapabilities"], ["SYS_PTRACE", "PERFMON"])
            self.assertEqual(topology["host"]["kernel"], "6.8")
        finally:
            path.unlink(missing_ok=True)

    def test_markdown_reports_missing_captures_as_incomplete(self):
        report = {"outcomes": [{"name": "cpu", "status": "passed", "detail": "ok"}], "counts": {"passed": 1}}
        text = ev.render_markdown(report, ["cpu", "off_cpu"])
        self.assertIn("Incomplete coverage", text)
        self.assertIn("| off_cpu | missing |", text)


if __name__ == "__main__":
    unittest.main()
