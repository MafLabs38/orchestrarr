import unittest

import check

REPO = "Org/app"
APPROVERS = {"maintainer"}
URL = "https://github.com/Org/app/pull/7#issuecomment-99"


def entry(level="high", decision="declined", **fields):
    base = {"Finding": "a finding", "Where": "src/a.cs", "Reason": "it does not apply to this product"}
    base.update(fields)
    return {"id": "R-7-1", "agent": "security", "level": level, "decision": decision, "fields": base, "line": 1}


def problems(item, author="maintainer", on=7, text="OK to decline R-7-1"):
    return check.check_entry(item, lambda *_: None if author is None else (author, on, text), APPROVERS, REPO, 7)


class CheckEntry(unittest.TestCase):
    def test_a_fixed_finding_needs_nothing_more(self):
        self.assertEqual(problems(entry("high", "fixed", Reason="")), [])

    def test_a_minor_declined_finding_needs_its_reason_only(self):
        self.assertEqual(problems(entry("minor")), [])
        self.assertTrue(problems(entry("minor", Reason="no")))

    def test_a_grave_declined_finding_needs_the_maintainers_comment(self):
        self.assertTrue(problems(entry("high")))
        self.assertEqual(problems(entry("high", Approved=URL)), [])
        self.assertEqual(problems(entry("major", "deferred", Approved=URL)), [])

    def test_the_comment_must_be_by_an_approver(self):
        self.assertTrue(problems(entry("critical", Approved=URL), author="the-agent"))

    def test_the_comment_must_be_readable_and_of_this_repository(self):
        self.assertTrue(problems(entry("high", Approved=URL), author=None))
        self.assertTrue(problems(entry("high", Approved=URL.replace("Org/app", "Org/other"))))
        self.assertTrue(problems(entry("high", Approved="https://example.com/x#issuecomment-1")))

    def test_the_comment_must_be_on_this_pull_request(self):
        self.assertTrue(problems(entry("high", Approved=URL), on=12))

    def test_the_comment_must_say_ok_to_decline_this_exact_finding(self):
        self.assertTrue(problems(entry("high", Approved=URL), text="please fix R-7-1"))
        self.assertTrue(problems(entry("high", Approved=URL), text="looks good to me"))
        self.assertTrue(problems(entry("high", Approved=URL), text="OK to decline R-7-10"), "R-7-1 is not R-7-10")
        self.assertTrue(problems(entry("high", Approved=URL), text="do NOT decline R-7-1, fix it"))
        self.assertEqual(problems(entry("high", Approved=URL), text="OK to defer R-7-1, thanks"), [])
        self.assertEqual(problems(entry("high", Approved=URL), text="OK to decline R-7-1"), [])

    def test_an_id_with_a_leading_zero_is_not_an_entry(self):
        _, found = check.parse("## R-7-01 | security | high | declined\n")
        self.assertEqual(len(found), 1)

    def test_the_agent_is_one_of_the_known_sources(self):
        _, found = check.parse("## R-7-1 | someone | high | declined\n")
        self.assertEqual(len(found), 1)
        _, found = check.parse("## R-7-1 | codeql | high | fixed\n")
        self.assertEqual(found, [])

    def test_an_unreadable_answer_is_a_clean_problem(self):
        self.assertTrue(problems(entry("high", Approved=URL), author=None))

    def test_an_unknown_level_or_decision_is_refused(self):
        self.assertTrue(problems(entry("huge")))
        self.assertTrue(problems(entry("high", "ignored")))


class ChangedFiles(unittest.TestCase):
    def test_a_renamed_and_edited_file_is_still_checked(self):
        import os, subprocess, tempfile
        with tempfile.TemporaryDirectory() as folder:
            def git(*args):
                subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", *args], cwd=folder, check=True, capture_output=True)
            git("init", "-q", "-b", "main")
            os.makedirs(os.path.join(folder, check.FOLDER))
            old = os.path.join(folder, check.FOLDER, "12.md")
            open(old, "w").write("## R-12-1 | security | low | fixed\n- **Finding**: f\n- **Where**: w\n")
            git("add", "-A")
            git("commit", "-q", "-m", "base")
            git("update-ref", "refs/remotes/origin/main", "HEAD")
            git("mv", os.path.join(check.FOLDER, "12.md"), os.path.join(check.FOLDER, "36.md"))
            open(os.path.join(folder, check.FOLDER, "36.md"), "a").write("## R-36-1 | security | high | declined\n- **Finding**: f\n- **Where**: w\n- **Reason**: no reason given here\n")
            git("add", "-A")
            git("commit", "-q", "-m", "rename")
            cwd = os.getcwd()
            os.chdir(folder)
            try:
                self.assertEqual(check.changed_files("main"), [os.path.join(check.FOLDER, "36.md")])
            finally:
                os.chdir(cwd)


class Parse(unittest.TestCase):
    def test_reads_entries_and_refuses_a_loose_heading(self):
        text = "# t\n## R-7-1 | security | high | declined\n- **Finding**: f\n- **Where**: w\n## oops\n"
        entries, found = check.parse(text)
        self.assertEqual([e["id"] for e in entries], ["R-7-1"])
        self.assertEqual(entries[0]["fields"]["Where"], "w")
        self.assertEqual(len(found), 1)


if __name__ == "__main__":
    unittest.main()
