"""Exercise the Windows installer downloader without network or new test dependencies."""

import http.server
from pathlib import Path
import shutil
import socket
import subprocess
import threading
import unittest
import uuid


ROOT = Path(__file__).resolve().parents[2]
PWSH = shutil.which("pwsh")


@unittest.skipUnless(PWSH, "PowerShell is required for the Windows downloader tests")
class InstallerDownloadTests(unittest.TestCase):
    def run_case(self, failure, failures=1, expected_attempts=2, expected_delays="2",
                 should_fail=False):
        script = r"""
        $ErrorActionPreference = 'Stop'
        . ./scripts/download-dotnet-installer.ps1
        $script:attempts = 0
        $script:delays = @()
        $script:original = $null
        function Invoke-WebRequest {
            param($Uri, $OutFile, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $ErrorAction)
            if ($Uri -ne 'https://dot.net/v1/dotnet-install.ps1' -or
                $OutFile -ne 'installer.ps1' -or $ErrorAction -ne 'Stop' -or
                $ConnectionTimeoutSeconds -ne 30 -or $OperationTimeoutSeconds -ne 60) {
                throw 'Unexpected download parameters'
            }
            $script:attempts++
            if ($script:attempts -le FAILURES) {
                $script:original = FAILURE
                throw $script:original
            }
        }
        function Start-Sleep {
            param($Seconds)
            $script:delays += $Seconds
        }
        $caught = $null
        try { Save-DotnetInstallScript -OutFile installer.ps1 }
        catch { $caught = $_.Exception }
        if ($script:attempts -ne ATTEMPTS) { throw "Attempts: $script:attempts" }
        if (($script:delays -join ',') -ne 'DELAYS') { throw "Delays: $script:delays" }
        if (SHOULD_FAIL) {
            if ($null -eq $caught -or -not [object]::ReferenceEquals($caught, $script:original)) {
                throw "Original error not preserved: $caught"
            }
        } elseif ($null -ne $caught) { throw $caught }
        """
        script = (script.replace("FAILURES", str(failures)).replace("FAILURE", failure)
                  .replace("ATTEMPTS", str(expected_attempts))
                  .replace("DELAYS", expected_delays)
                  .replace("SHOULD_FAIL", "$true" if should_fail else "$false"))
        result = subprocess.run([PWSH, "-NoLogo", "-NoProfile", "-NonInteractive",
                                 "-Command", script], cwd=ROOT, capture_output=True,
                                text=True, timeout=30, check=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_success_is_not_retried(self):
        self.run_case("[Exception]::new('unused')", failures=0,
                      expected_attempts=1, expected_delays="")

    def test_connection_reset_recovers_with_bounded_backoff(self):
        self.run_case(
            "[System.IO.IOException]::new('transport closed', "
            "[System.Net.Sockets.SocketException]::new(10054))",
            failures=2, expected_attempts=3, expected_delays="2,4")

    def test_interrupted_response_recovers(self):
        self.run_case(
            "[System.Net.Http.HttpIOException]::new("
            "[System.Net.Http.HttpRequestError]::ResponseEnded, 'partial body')")

    def test_connection_error_recovers(self):
        self.run_case(
            "[System.Net.Http.HttpRequestException]::new("
            "[System.Net.Http.HttpRequestError]::ConnectionError, 'connection failed')")

    def test_exhaustion_preserves_final_transport_exception(self):
        self.run_case(
            "[System.Net.Sockets.SocketException]::new(10054)",
            failures=10, expected_attempts=3, expected_delays="2,4", should_fail=True)

    def test_http_status_failures_are_not_retried(self):
        for status in (404, 429, 500, 503):
            with self.subTest(status=status):
                self.run_case(
                    "[System.Net.Http.HttpRequestException]::new("
                    f"'HTTP error', $null, [System.Net.HttpStatusCode]{status})",
                    expected_attempts=1, expected_delays="", should_fail=True)

    def test_disk_and_permission_errors_are_not_retried(self):
        for error in ("System.IO.IOException", "System.UnauthorizedAccessException"):
            with self.subTest(error=error):
                self.run_case(f"[{error}]::new('local file error')",
                              expected_attempts=1, expected_delays="", should_fail=True)

    def test_tls_validation_error_is_not_retried(self):
        self.run_case(
            "[System.Net.Http.HttpRequestException]::new("
            "[System.Net.Http.HttpRequestError]::ConnectionError, 'TLS failure', "
            "[System.Security.Authentication.AuthenticationException]::new('certificate'))",
            expected_attempts=1, expected_delays="", should_fail=True)

    def test_installer_execution_stays_outside_retry(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text()
        self.assertIn("Save-DotnetInstallScript\n          ./dotnet-install.ps1", workflow)
        self.assertEqual(workflow.count("-SkipNonVersionedFiles"), 2)
        helper = (ROOT / "scripts/download-dotnet-installer.ps1").read_text()
        self.assertNotIn("./dotnet-install.ps1", helper)

    def test_real_partial_response_is_replaced_by_complete_download(self):
        payload = b"# synthetic installer; never executed\n"

        class Handler(http.server.BaseHTTPRequestHandler):
            attempts = 0

            def do_GET(self):
                Handler.attempts += 1
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload[:1] if Handler.attempts == 1 else payload)
                self.wfile.flush()
                if Handler.attempts == 1:
                    self.connection.shutdown(socket.SHUT_RDWR)
                    self.connection.close()

            def log_message(self, *_args):
                pass

        directory = ROOT / "artifacts/installer-download-tests" / uuid.uuid4().hex
        directory.mkdir(parents=True)
        output = directory / "installer.ps1"
        server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever)
        thread.start()
        try:
            script = r"""
            $ErrorActionPreference = 'Stop'
            . ./scripts/download-dotnet-installer.ps1
            function Invoke-WebRequest {
                param($Uri, $OutFile, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $ErrorAction)
                Microsoft.PowerShell.Utility\Invoke-WebRequest -Uri http://127.0.0.1:PORT `
                    -OutFile $OutFile -ConnectionTimeoutSeconds $ConnectionTimeoutSeconds `
                    -OperationTimeoutSeconds $OperationTimeoutSeconds -ErrorAction Stop
            }
            function Start-Sleep { param($Seconds) }
            Save-DotnetInstallScript -OutFile 'OUTPUT'
            """
            script = script.replace("PORT", str(server.server_port)).replace(
                "OUTPUT", output.relative_to(ROOT).as_posix())
            result = subprocess.run([PWSH, "-NoLogo", "-NoProfile", "-NonInteractive",
                                     "-Command", script], cwd=ROOT, capture_output=True,
                                    text=True, timeout=30, check=False)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(Handler.attempts, 2)
            self.assertEqual(output.read_bytes(), payload)
        finally:
            server.shutdown()
            thread.join()
            server.server_close()
            shutil.rmtree(directory)


if __name__ == "__main__":
    unittest.main()
