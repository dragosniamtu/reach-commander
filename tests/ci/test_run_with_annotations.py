from __future__ import annotations

from io import StringIO
import sys
import unittest

from tools.run_with_annotations import run_command


class RunWithAnnotationsTests(unittest.TestCase):
    def test_success_preserves_output_without_an_error_annotation(self) -> None:
        output = StringIO()

        exit_code = run_command(
            [sys.executable, "-c", "print('renderer ok')"],
            "Renderer failed",
            output,
        )

        self.assertEqual(0, exit_code)
        self.assertEqual("renderer ok\n", output.getvalue())

    def test_failure_preserves_exit_code_and_emits_escaped_output(self) -> None:
        output = StringIO()

        exit_code = run_command(
            [
                sys.executable,
                "-c",
                "import sys; print('Expected: 100%\\nActual: 50%'); sys.exit(3)",
            ],
            "Renderer: failed, Linux",
            output,
        )

        self.assertEqual(3, exit_code)
        self.assertEqual(
            "Expected: 100%\n"
            "Actual: 50%\n"
            "::error title=Renderer%3A failed%2C Linux::"
            "Expected: 100%25%0AActual: 50%25\n",
            output.getvalue(),
        )

    def test_long_failure_annotation_keeps_the_diagnostic_tail(self) -> None:
        output = StringIO()

        exit_code = run_command(
            [
                sys.executable,
                "-c",
                "import sys; print('A' * 4000 + 'TRACEBACK END'); sys.exit(1)",
            ],
            "Long failure",
            output,
        )

        annotation = output.getvalue().splitlines()[-1]
        self.assertEqual(1, exit_code)
        self.assertIn("TRACEBACK END", annotation)
        self.assertIn("::...", annotation)


if __name__ == "__main__":
    unittest.main()
