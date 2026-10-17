"""Unit tests for scripts/release_plan.py. Run: python3 -m unittest discover -s scripts -p "test_*.py"."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import release_plan as rp  # noqa: E402

MAIN = "refs/heads/main"
SHA = "a" * 40
OTHER = "b" * 40


def plan(event="push", ref=MAIN, current="0.2", previous="0.1", build="57", publish=False, tags=None):
    return rp.plan(event=event, ref=ref, sha=SHA, current=current, previous=previous, build=build,
                   publish_requested=publish, tags=tags or {})


class ReleasePlanTests(unittest.TestCase):
    def test_a_version_change_on_main_publishes_its_own_tag(self):
        result = plan()
        self.assertTrue(result.publish)
        self.assertEqual(("0.2.57", "0.2", "v0.2.57"), (result.version, result.xy, result.tag))
        self.assertEqual("Noctaxis 0.2.57 (alpha)", result.outputs()["title"])
        self.assertTrue(result.prerelease)

    def test_the_version_bump_workflow_publishes_like_a_push(self):
        self.assertTrue(plan(event="version-bump").publish)

    def test_one_and_later_are_not_prereleases(self):
        result = plan(current="1.0", previous="0.9")
        self.assertFalse(result.prerelease)
        self.assertEqual("Noctaxis 1.0.57", result.outputs()["title"])

    def test_an_unchanged_or_unknown_previous_version_only_verifies(self):
        self.assertFalse(plan(previous="0.2").publish)
        self.assertFalse(plan(previous=None).publish)

    def test_other_branches_never_publish(self):
        self.assertFalse(plan(ref="refs/heads/dev").publish)
        self.assertFalse(plan(event="workflow_dispatch", ref="refs/heads/dev").publish)
        with self.assertRaises(rp.PlanError):
            plan(event="workflow_dispatch", ref="refs/heads/dev", publish=True)

    def test_a_repeated_push_of_a_released_version_does_not_publish_again(self):
        result = plan(tags={"v0.2.40": OTHER})
        self.assertFalse(result.publish)
        self.assertIn("v0.2.40", result.reason)

    def test_a_rerun_of_the_same_release_continues(self):
        # Re-running a run keeps its number, so a half-finished release completes under the same tag.
        self.assertTrue(plan(tags={"v0.2.57": SHA}).publish)
        self.assertTrue(plan(event="workflow_dispatch", publish=True, tags={"v0.2.57": SHA}).publish)

    def test_a_tag_on_another_commit_fails_instead_of_moving(self):
        with self.assertRaises(rp.PlanError):
            plan(tags={"v0.2.57": OTHER})
        with self.assertRaises(rp.PlanError):
            plan(event="workflow_dispatch", publish=True, tags={"v0.2.57": OTHER})
        # Only publishing needs the tag: a run that publishes nothing still verifies.
        self.assertFalse(plan(event="workflow_dispatch", tags={"v0.2.57": OTHER}).publish)

    def test_manual_runs_publish_only_when_asked(self):
        self.assertFalse(plan(event="workflow_dispatch", previous=None).publish)
        self.assertTrue(plan(event="workflow_dispatch", previous=None, publish=True).publish)

    def test_publishing_a_released_version_by_hand_fails(self):
        with self.assertRaises(rp.PlanError):
            plan(event="workflow_dispatch", publish=True, tags={"v0.2.40": OTHER})

    def test_versions_never_go_backwards(self):
        with self.assertRaises(rp.PlanError):
            plan(current="0.1", previous="0.2")
        with self.assertRaises(rp.PlanError):
            plan(current="0.2", previous="0.1", tags={"v0.3.10": OTHER})
        with self.assertRaises(rp.PlanError):
            plan(event="workflow_dispatch", publish=True, current="0.2", tags={"v1.0.3": OTHER})

    def test_other_tags_are_ignored(self):
        # Only vX.Y.Z tags are releases, and 0.20 is not 0.2.
        self.assertTrue(plan(tags={"v0.2": OTHER, "v0.2.x": OTHER, "release-0.2.1": OTHER}).publish)
        self.assertTrue(plan(current="0.21", previous="0.20", tags={"v0.20.1": OTHER, "v0.2.9": OTHER}).publish)

    def test_unknown_events_fail(self):
        with self.assertRaises(rp.PlanError):
            plan(event="pull_request")


if __name__ == "__main__":
    unittest.main()
