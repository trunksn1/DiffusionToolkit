import json
import unittest

from api_client import _decode_reference_table, _extract_trpc_json


class TrpcResponseParsingTests(unittest.TestCase):
    def test_extracts_legacy_json_wrapper(self):
        response = {
            "result": {
                "data": {
                    "json": {
                        "nextCursor": 42,
                        "items": [{"id": 7, "url": "image-url"}],
                    }
                }
            }
        }

        payload = _extract_trpc_json(response)

        self.assertEqual(42, payload["nextCursor"])
        self.assertEqual(7, payload["items"][0]["id"])

    def test_decodes_current_reference_table_wrapper(self):
        table = [
            {"nextCursor": 1, "items": 2},
            42,
            [3],
            {"id": 4, "url": 5, "hasMeta": 6},
            7,
            "image-url",
            True,
        ]
        response = {"result": {"data": json.dumps(table)}}

        payload = _extract_trpc_json(response)

        self.assertEqual(42, payload["nextCursor"])
        self.assertEqual(
            {"id": 7, "url": "image-url", "hasMeta": True},
            payload["items"][0],
        )

    def test_decoder_reuses_references(self):
        shared = _decode_reference_table([{"first": 1, "second": 1}, {"id": 2}, 7])

        self.assertIs(shared["first"], shared["second"])


if __name__ == "__main__":
    unittest.main()