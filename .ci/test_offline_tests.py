import tempfile
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

import offline_tests as ci


class OfflineResultsTests(unittest.TestCase):
    def report(self, outcome="Passed", omit=False, extra=False):
        classes, _ = ci.selection()
        if omit:
            classes = classes[:-1]
        if extra:
            classes.append("NoPonto.Tests.TelemetriaMlIntegracaoTests")
        root = ET.Element("TestRun", xmlns=ci.NS["t"])
        summary = ET.SubElement(root, "ResultSummary")
        ET.SubElement(summary, "Counters", total=str(len(classes)), passed=str(len(classes)))
        definitions = ET.SubElement(root, "TestDefinitions")
        results = ET.SubElement(root, "Results")
        for index, name in enumerate(classes):
            test = ET.SubElement(definitions, "UnitTest", id=str(index))
            ET.SubElement(test, "TestMethod", className=name, name="Synthetic")
            ET.SubElement(results, "UnitTestResult", testId=str(index), outcome=outcome)
        return ET.tostring(root)

    def validate(self, content):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "synthetic.trx"
            path.write_bytes(content)
            return ci.verify(path)

    def test_accepts_all_selected_classes(self):
        self.assertEqual(len(ci.selection()[0]), self.validate(self.report()))

    def test_rejects_missing_class(self):
        with self.assertRaises(ValueError):
            self.validate(self.report(omit=True))

    def test_rejects_connected_class(self):
        with self.assertRaises(ValueError):
            self.validate(self.report(extra=True))

    def test_rejects_failed_or_skipped_tests(self):
        for outcome in ("Failed", "NotExecuted"):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                self.validate(self.report(outcome=outcome))

    def test_rejects_empty_report(self):
        with self.assertRaises(ValueError):
            self.validate(b'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"/>')

    def test_filter_bounds_classes_and_excludes_replay(self):
        value = ci.test_filter()
        for name in ci.selection()[0]:
            self.assertIn(f"FullyQualifiedName~{name}.", value)
        self.assertIn("&FullyQualifiedName!=", value)


if __name__ == "__main__":
    unittest.main()
