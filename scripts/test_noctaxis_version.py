"""Unit tests for scripts/noctaxis_version.py. Run: python3 -m unittest discover -s scripts -p "test_*.py"."""
import io
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import noctaxis_version as nv  # noqa: E402

PROPS = (
    "<Project>\r\n"
    "  <!-- comment mentioning NoctaxisVersion must survive -->\r\n"
    "  <PropertyGroup>\r\n"
    "    <NoctaxisVersion>0.4</NoctaxisVersion>\r\n"
    "    <NoctaxisBuildNumber Condition=\"'$(NoctaxisBuildNumber)' == ''\">0</NoctaxisBuildNumber>\r\n"
    "  </PropertyGroup>\r\n"
    "</Project>\r\n"
)


class VersionLogicTests(unittest.TestCase):
    def test_compose_matches_documented_examples(self):
        self.assertEqual("0.4.0", nv.compose("0.4", None))      # local build fallback
        self.assertEqual("0.4.0", nv.compose("0.4", ""))
        self.assertEqual("0.4.173", nv.compose("0.4", 173))     # CI build #173
        self.assertEqual("0.5.174", nv.compose(nv.increment("0.4"), 174))  # after dev -> main

    def test_increment_changes_only_y_and_never_carries_into_x(self):
        self.assertEqual("0.5", nv.increment("0.4"))
        self.assertEqual("0.10", nv.increment("0.9"))
        self.assertEqual("3.100", nv.increment("3.99"))
        with self.assertRaises(nv.VersionError):
            nv.increment("1.65534")

    def test_parse_rejects_values_msbuild_would_reject(self):
        for bad in ["", "1", "1.2.3", "01.2", "1.02", "-1.2", "a.b", " 1 .2", "65535.0"]:
            with self.subTest(bad=bad), self.assertRaises(nv.VersionError):
                nv.parse(bad)
        self.assertEqual((0, 0), nv.parse("0.0"))

    def test_compose_rejects_invalid_build_numbers(self):
        for bad in ["-1", "01", "1.5", "65535", "x"]:
            with self.subTest(bad=bad), self.assertRaises(nv.VersionError):
                nv.compose("0.4", bad)
        self.assertEqual("0.4.65534", nv.compose("0.4", 65534))

    def test_replace_rewrites_only_the_version_text(self):
        updated = nv.replace_in(PROPS, "0.5")
        self.assertEqual(PROPS.replace(">0.4<", ">0.5<"), updated)
        self.assertEqual("0.5", nv.read_from(updated))

    def test_read_requires_exactly_one_element(self):
        with self.assertRaises(nv.VersionError):
            nv.read_from("<Project></Project>")
        with self.assertRaises(nv.VersionError):
            nv.read_from(PROPS + "<NoctaxisVersion>0.4</NoctaxisVersion>")
        with self.assertRaises(nv.VersionError):
            nv.read_from(PROPS.replace(">0.4<", ">0.4.1<"))


class CommandLineTests(unittest.TestCase):
    def setUp(self):
        self._directory = tempfile.TemporaryDirectory()
        self.file = Path(self._directory.name) / "Directory.Build.props"
        self.file.write_bytes(PROPS.encode("utf-8"))

    def tearDown(self):
        self._directory.cleanup()

    def run_tool(self, *args):
        output = io.StringIO()
        with redirect_stdout(output):
            code = nv.main([*args, "--file", str(self.file)])
        return code, output.getvalue().strip()

    def test_dev_to_main_example_end_to_end(self):
        self.assertEqual((0, "0.4.0"), self.run_tool("compose"))
        self.assertEqual((0, "0.4.173"), self.run_tool("compose", "--build", "173"))
        self.assertEqual((0, "0.5"), self.run_tool("bump"))         # main after the merge
        self.assertEqual((0, "0.5"), self.run_tool("get"))
        self.assertEqual((0, "0.5.174"), self.run_tool("compose", "--build", "174"))
        # Line endings and all other content are preserved byte for byte.
        self.assertEqual(PROPS.replace(">0.4<", ">0.5<").encode("utf-8"), self.file.read_bytes())

    def test_set_applies_main_version_to_dev_copy(self):
        self.assertEqual((0, "0.5"), self.run_tool("set", "0.5"))
        self.assertEqual((0, "0.5"), self.run_tool("get"))

    def test_invalid_file_fails_without_writing(self):
        broken = PROPS.replace(">0.4<", ">zero<")
        self.file.write_bytes(broken.encode("utf-8"))
        code, _ = self.run_tool("bump")
        self.assertEqual(1, code)
        self.assertEqual(broken.encode("utf-8"), self.file.read_bytes())


if __name__ == "__main__":
    unittest.main()
