from __future__ import annotations

from io import StringIO
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from tools.report_trx import report


TRX_DOCUMENT = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="ReachCommander.Tests.Passes" outcome="Passed" />
    <UnitTestResult testName="ReachCommander.Tests.Fails: case, one" outcome="Failed">
      <Output>
        <ErrorInfo>
          <Message>Expected: 100%
Actual: 50%</Message>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
  </Results>
</TestRun>
"""


class ReportTrxTests(unittest.TestCase):
    def test_reports_only_failed_results_as_escaped_error_annotations(self) -> None:
        with TemporaryDirectory() as directory:
            path = Path(directory, "backend.trx")
            path.write_text(TRX_DOCUMENT, encoding="utf-8")
            output = StringIO()

            report([path], output)

        lines = output.getvalue().splitlines()
        self.assertEqual(1, len(lines))
        self.assertEqual(
            "::error title=.NET test failed - ReachCommander.Tests.Fails%3A case%2C one::"
            "Expected: 100%25%0AActual: 50%25",
            lines[0],
        )
        self.assertNotIn("Passes", lines[0])

    def test_reports_when_a_trx_contains_no_failed_results(self) -> None:
        with TemporaryDirectory() as directory:
            path = Path(directory, "backend.trx")
            path.write_text(
                "<TestRun><Results><UnitTestResult testName=\"ok\" outcome=\"Passed\" />"
                "</Results></TestRun>",
                encoding="utf-8",
            )
            output = StringIO()

            report([path], output)

        self.assertEqual("No failed .NET test details were found in backend.trx.\n", output.getvalue())

    def test_empty_failure_message_uses_a_safe_fallback(self) -> None:
        with TemporaryDirectory() as directory:
            path = Path(directory, "backend.trx")
            path.write_text(
                "<TestRun><Results><UnitTestResult testName=\"failed\" outcome=\"Failed\">"
                "<Output><ErrorInfo><Message /></ErrorInfo></Output>"
                "</UnitTestResult></Results></TestRun>",
                encoding="utf-8",
            )
            output = StringIO()

            report([path], output)

        self.assertEqual(
            "::error title=.NET test failed - failed::No failure message was recorded.\n",
            output.getvalue(),
        )

    def test_missing_trx_emits_a_warning_without_raising(self) -> None:
        output = StringIO()

        report([Path("missing.trx")], output)

        self.assertEqual(
            "::warning title=.NET diagnostics unavailable::TRX file was not found: missing.trx\n",
            output.getvalue(),
        )

    def test_malformed_trx_emits_a_warning_without_raising(self) -> None:
        with TemporaryDirectory() as directory:
            path = Path(directory, "broken.trx")
            path.write_text("<TestRun>", encoding="utf-8")
            output = StringIO()

            report([path], output)

        self.assertEqual(
            "::warning title=.NET diagnostics unavailable::Could not parse TRX file: broken.trx\n",
            output.getvalue(),
        )


if __name__ == "__main__":
    unittest.main()
