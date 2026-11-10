import unittest

import check

REPO = "Org/app"
APPROVERS = {"maintainer"}
URL = "https://github.com/Org/app/pull/7#issuecomment-99"


def entry(level="high", decision="declined", **fields):
    base = {"Finding": "a finding", "Where": "src/a.cs", "Reason": "it does not apply to this product"}
    base.update(fields)
    return {"id": "R-7-1", "agent": "security", "level": level, "decision": decision, "fields": base, "line": 1}


def problems(item, author="maintainer"):
    return check.check_entry(item, lambda *_: author, APPROVERS, REPO)


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

    def test_an_unknown_level_or_decision_is_refused(self):
        self.assertTrue(problems(entry("huge")))
        self.assertTrue(problems(entry("high", "ignored")))


class Parse(unittest.TestCase):
    def test_reads_entries_and_refuses_a_loose_heading(self):
        text = "# t\n## R-7-1 | security | high | declined\n- **Finding**: f\n- **Where**: w\n## oops\n"
        entries, found = check.parse(text)
        self.assertEqual([e["id"] for e in entries], ["R-7-1"])
        self.assertEqual(entries[0]["fields"]["Where"], "w")
        self.assertEqual(len(found), 1)


if __name__ == "__main__":
    unittest.main()
