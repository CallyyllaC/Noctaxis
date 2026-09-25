"""Integration tests for scripts/version-bump-after-merge.sh against throwaway local git remotes.

Run: python3 -m unittest discover -s scripts -p "test_*.py"   (requires git and bash)
"""
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
SCRIPT = SCRIPTS / "version-bump-after-merge.sh"
TOOL = SCRIPTS / "noctaxis_version.py"
PROPS = "<Project>\n  <PropertyGroup>\n    <NoctaxisVersion>{}</NoctaxisVersion>\n  </PropertyGroup>\n</Project>\n"
TRAILER = "Noctaxis-Version-Bump-PR: {}"


@unittest.skipUnless(shutil.which("git") and shutil.which("bash"), "git and bash are required")
class VersionBumpAfterMergeTests(unittest.TestCase):
    def setUp(self):
        self._temp = tempfile.TemporaryDirectory()
        root = Path(self._temp.name)
        self.remote = root / "remote.git"
        self.author = root / "author"   # stands in for developers and GitHub's PR merge
        self.runner = root / "runner"   # stands in for the Actions checkout
        self.env = {**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
                    "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t", "GIT_CONFIG_NOSYSTEM": "1"}
        self.git(root, "init", "--quiet", "--bare", "--initial-branch=main", str(self.remote))
        self.git(root, "clone", "--quiet", str(self.remote), str(self.author))
        self.write_version(self.author, "0.4")
        self.git(self.author, "add", ".")
        self.git(self.author, "commit", "--quiet", "-m", "initial")
        self.git(self.author, "push", "--quiet", "origin", "HEAD:main", "HEAD:dev")
        self.git(root, "clone", "--quiet", str(self.remote), str(self.runner))
        # Stand-in for scripts/verify-msbuild-version.sh (CI has dotnet; these tests need not).
        # It resolves X.Y.0 from the working tree the same way and logs each verification.
        self.verify_log = root / "verified.log"
        self.verifier = root / "verify.sh"
        self.verifier.write_text(
            "#!/usr/bin/env bash\n"
            f'echo "$1 $(git rev-parse --abbrev-ref HEAD)" >> "{self.verify_log}"\n'
            '[ -z "${FAIL_VERIFY:-}" ] || exit 1\n'
            f'[ "$(python3 "{TOOL}" compose --file Directory.Build.props)" = "$1" ]\n',
            encoding="utf-8")
        self.verifier.chmod(0o755)

    def tearDown(self):
        self._temp.cleanup()

    # -- helpers -----------------------------------------------------------------------------
    def git(self, cwd, *args):
        return subprocess.run(["git", *args], cwd=cwd, env=self.env, check=True,
                              capture_output=True, text=True).stdout.strip()

    def write_version(self, clone, version):
        (Path(clone) / "Directory.Build.props").write_text(PROPS.format(version), encoding="utf-8")

    def version_on(self, branch):
        self.git(self.author, "fetch", "--quiet", "origin")
        text = self.git(self.author, "show", f"origin/{branch}:Directory.Build.props")
        return text.split("<NoctaxisVersion>")[1].split("<")[0]

    def merge_dev_into_main(self, feature="feature.txt"):
        """Commit on dev, then merge dev into main as a PR merge would; returns the merge SHA."""
        self.git(self.author, "fetch", "--quiet", "origin")
        self.git(self.author, "checkout", "--quiet", "-B", "dev", "origin/dev")
        (self.author / feature).write_text(feature, encoding="utf-8")
        self.git(self.author, "add", ".")
        self.git(self.author, "commit", "--quiet", "-m", f"work {feature}")
        self.git(self.author, "push", "--quiet", "origin", "HEAD:dev")
        self.git(self.author, "checkout", "--quiet", "-B", "main", "origin/main")
        self.git(self.author, "merge", "--quiet", "--no-ff", "-m", "Merge dev", "dev")
        self.git(self.author, "push", "--quiet", "origin", "HEAD:main")
        return self.git(self.author, "rev-parse", "HEAD")

    def run_bump(self, pr, merge_sha, **extra):
        env = {**self.env, "PR_NUMBER": str(pr), "MERGE_SHA": merge_sha, "VERSION_TOOL": str(TOOL),
               "VERIFY_VERSION": str(self.verifier), "MAX_ATTEMPTS": "3", **extra}
        return subprocess.run(["bash", str(SCRIPT)], cwd=self.runner, env=env, capture_output=True, text=True)

    def commit_count(self, branch):
        self.git(self.author, "fetch", "--quiet", "origin")
        return int(self.git(self.author, "rev-list", "--count", f"origin/{branch}"))

    def install_hook(self, name, body):
        hook = self.remote / "hooks" / name
        hook.write_text("#!/usr/bin/env bash\n" + body, encoding="utf-8")
        hook.chmod(0o755)

    # -- scenarios ---------------------------------------------------------------------------
    def test_merge_increments_y_once_and_applies_same_version_to_dev(self):
        merge = self.merge_dev_into_main()
        result = self.run_bump(7, merge)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("0.5", self.version_on("main"))
        self.assertEqual("0.5", self.version_on("dev"))
        message = self.git(self.author, "log", "-1", "--format=%B", "origin/main")
        self.assertIn(TRAILER.format(7), message.splitlines())

    def test_each_version_commit_is_verified_before_it_is_pushed(self):
        result = self.run_bump(7, self.merge_dev_into_main())
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["0.5.0 main", "0.5.0 dev"], self.verify_log.read_text().splitlines())

    def test_failed_verification_pushes_nothing(self):
        merge = self.merge_dev_into_main()
        main_commits = self.commit_count("main")
        result = self.run_bump(7, merge, FAIL_VERIFY="1")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("nothing was pushed to main", result.stderr)
        self.assertEqual(("0.4", "0.4"), (self.version_on("main"), self.version_on("dev")))
        self.assertEqual(main_commits, self.commit_count("main"))

    def test_rerun_for_the_same_pull_request_does_not_bump_again(self):
        merge = self.merge_dev_into_main()
        self.assertEqual(0, self.run_bump(7, merge).returncode)
        main_commits, dev_commits = self.commit_count("main"), self.commit_count("dev")
        result = self.run_bump(7, merge)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(("0.5", "0.5"), (self.version_on("main"), self.version_on("dev")))
        self.assertEqual((main_commits, dev_commits), (self.commit_count("main"), self.commit_count("dev")))

    def test_pull_request_number_prefix_is_not_mistaken_for_an_earlier_bump(self):
        self.assertEqual(0, self.run_bump(12, self.merge_dev_into_main("a.txt")).returncode)
        self.assertEqual(0, self.run_bump(1, self.merge_dev_into_main("b.txt")).returncode)
        self.assertEqual(("0.6", "0.6"), (self.version_on("main"), self.version_on("dev")))

    def test_next_merge_after_sync_bumps_again_without_conflict(self):
        self.assertEqual(0, self.run_bump(1, self.merge_dev_into_main("a.txt")).returncode)
        self.assertEqual(0, self.run_bump(2, self.merge_dev_into_main("b.txt")).returncode)
        self.assertEqual(("0.6", "0.6"), (self.version_on("main"), self.version_on("dev")))

    def test_manual_version_change_on_dev_is_never_overwritten(self):
        merge = self.merge_dev_into_main()
        self.git(self.author, "checkout", "--quiet", "-B", "dev", "origin/dev")
        self.write_version(self.author, "1.0")  # manual major change landed after the merge
        self.git(self.author, "commit", "--quiet", "-am", "major")
        self.git(self.author, "push", "--quiet", "origin", "HEAD:dev")
        result = self.run_bump(7, merge)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Refusing to overwrite", result.stderr)
        self.assertEqual(("0.5", "1.0"), (self.version_on("main"), self.version_on("dev")))

    def test_unknown_merge_commit_changes_nothing(self):
        self.merge_dev_into_main()
        result = self.run_bump(7, "0" * 40)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(("0.4", "0.4"), (self.version_on("main"), self.version_on("dev")))

    def test_protected_branch_rejection_is_reported_not_retried_or_forced(self):
        merge = self.merge_dev_into_main()
        self.install_hook("pre-receive", 'echo "protected branch" >&2; exit 1\n')
        result = self.run_bump(7, merge)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Branch protection or token permissions", result.stderr)
        self.assertNotIn("retrying", result.stdout)
        self.assertEqual(("0.4", "0.4"), (self.version_on("main"), self.version_on("dev")))

    def test_concurrent_update_of_main_is_retried_on_the_new_tip(self):
        merge = self.merge_dev_into_main()
        # Prepare an unrelated commit on top of main, then make the remote reject the first push
        # while advancing main to it, exactly as a concurrent push would.
        self.git(self.author, "checkout", "--quiet", "-B", "main", "origin/main")
        (self.author / "concurrent.txt").write_text("x", encoding="utf-8")
        self.git(self.author, "add", ".")
        self.git(self.author, "commit", "--quiet", "-m", "concurrent")
        concurrent = self.git(self.author, "rev-parse", "HEAD")
        self.git(self.author, "push", "--quiet", "origin", f"{concurrent}:refs/heads/staging")
        self.install_hook("pre-receive", f"""marker="$GIT_DIR/raced"
if [ ! -e "$marker" ]; then
  # Outside the push quarantine, as a separate concurrent writer would be.
  touch "$marker"; env -u GIT_QUARANTINE_PATH git update-ref refs/heads/main {concurrent}; exit 1
fi
""")
        result = self.run_bump(7, merge)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("retrying", result.stdout)
        self.assertEqual(("0.5", "0.5"), (self.version_on("main"), self.version_on("dev")))
        self.git(self.author, "fetch", "--quiet", "origin")
        self.assertEqual(concurrent, self.git(self.author, "rev-parse", "origin/main~1"))


if __name__ == "__main__":
    unittest.main()
