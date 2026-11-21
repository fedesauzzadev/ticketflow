"""python -m unittest discover infra/lambda/ticket_notifier  (AC3 de N-4002)"""
import unittest

from handler import handler


def rec(key, bucket="ticketflow-tickets"):
    return {"s3": {"bucket": {"name": bucket}, "object": {"key": key}}}


class HandlerTests(unittest.TestCase):
    def test_valid_key_notifies(self):
        self.assertEqual(handler({"Records": [rec("qr/pur-1/tkt-9.json")]}, None), {"notified": 1})

    def test_url_encoded_key(self):
        self.assertEqual(handler({"Records": [rec("qr/pur%2D1/tkt+9.json")]}, None), {"notified": 1})

    def test_hostile_inputs_never_raise(self):
        hostile = [
            None, {}, [], "x", {"Records": None}, {"Records": "abc"}, {"Records": [None, 1, {}, {"s3": None}]},
            {"Records": [rec(""), rec("other/a/b.json"), rec("qr/a.json"), rec("qr/a/b/c.json"),
                         rec("qr/a/b.txt"), rec("qr//.json"), rec("qr/‮/\x00.json" * 5000), rec(None)]},
        ]
        for event in hostile:
            self.assertEqual(handler(event, None), {"notified": 0}, msg=repr(event)[:80])

    def test_mixed_batch_only_counts_valid(self):
        self.assertEqual(handler({"Records": [rec("junk"), rec("qr/p/t.json")]}, None), {"notified": 1})


if __name__ == "__main__":
    unittest.main()
