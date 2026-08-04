import json
import unittest

from api_client import CivitAIClient, _decode_reference_table, _extract_trpc_json


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


class _StubResponse:
    def __init__(self, status_code):
        self.status_code = status_code


class _StubSession:
    """Records the Authorization header of every GET, and replies to order."""

    def __init__(self, statuses):
        self._statuses = list(statuses)
        self.attempts = []

    def get(self, url, **kwargs):
        headers = kwargs.get("headers") or {}
        self.attempts.append(headers.get("Authorization"))
        return _StubResponse(self._statuses.pop(0) if self._statuses else 200)


def _client(access_token="", api_key=""):
    """A CivitAIClient with __init__ bypassed - it would do network I/O."""
    client = CivitAIClient.__new__(CivitAIClient)
    client.access_token = access_token
    client.api_key = api_key
    client._bearer_ok = {}
    client._cookies_loaded = True  # never load real cookies in a test
    return client


class CredentialRoutingTests(unittest.TestCase):
    """OAuth tokens authenticate tRPC but are rejected by REST v1, so the two
    families must not share a credential."""

    def test_trpc_prefers_the_oauth_token(self):
        client = _client(access_token="oauth", api_key="key")
        client.session = _StubSession([200])

        client._auth_get("https://civitai.com/api/trpc/x", family="trpc")

        self.assertEqual(["Bearer oauth"], client.session.attempts)

    def test_rest_never_offers_the_oauth_token(self):
        client = _client(access_token="oauth", api_key="key")
        client.session = _StubSession([200])

        client._auth_get("https://civitai.com/api/v1/x", family="rest")

        self.assertEqual(["Bearer key"], client.session.attempts)

    def test_trpc_falls_back_to_the_api_key_then_cookies(self):
        client = _client(access_token="oauth", api_key="key")
        client.session = _StubSession([401, 403, 200])

        client._auth_get("https://civitai.com/api/trpc/x", family="trpc")

        self.assertEqual(["Bearer oauth", "Bearer key", None], client.session.attempts)

    def test_rejection_is_memoized_per_credential(self):
        client = _client(access_token="oauth", api_key="key")
        client.session = _StubSession([401, 200, 200])

        client._auth_get("https://civitai.com/api/trpc/x", family="trpc")
        client._auth_get("https://civitai.com/api/trpc/y", family="trpc")

        # The rejected OAuth token is not retried on the second call.
        self.assertEqual(["Bearer oauth", "Bearer key", "Bearer key"],
                         client.session.attempts)

    def test_auth_mechanism_reports_the_credential_that_worked(self):
        client = _client(access_token="oauth", api_key="key")
        client.session = _StubSession([401, 200])

        client._auth_get("https://civitai.com/api/trpc/x", family="trpc")

        self.assertEqual("API key", client.auth_mechanism("trpc"))

    def test_auth_mechanism_reports_cookies_when_no_bearer_worked(self):
        client = _client()
        client.session = _StubSession([200])

        client._auth_get("https://civitai.com/api/trpc/x", family="trpc")

        self.assertEqual("cookies", client.auth_mechanism("trpc"))


if __name__ == "__main__":
    unittest.main()