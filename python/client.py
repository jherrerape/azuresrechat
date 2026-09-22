from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Iterable

import httpx
from azure.core.credentials import AccessToken, TokenCredential

from config import API_VERSION, ARM_SCOPE, DATA_PLANE_SCOPE, AgentOptions


@dataclass(frozen=True)
class ChatMessage:
    role: str
    content: str
    message_id: str | None = None
    is_complete: bool = True


@dataclass(frozen=True)
class PendingApproval:
    approval_id: str
    summary: str


class SreAgentClient:
    def __init__(self, credential: TokenCredential) -> None:
        self._credential = credential
        self._http = httpx.Client(timeout=httpx.Timeout(300.0, connect=30.0))
        self._data_token: AccessToken | None = None
        self.endpoint: str = ""

    def __enter__(self) -> "SreAgentClient":
        return self

    def __exit__(self, *_exc: object) -> None:
        self._http.close()

    def resolve_endpoint(self, options: AgentOptions) -> str:
        if options.agent_endpoint:
            self.endpoint = _normalize(options.agent_endpoint)
            return self.endpoint

        url = (
            f"https://management.azure.com/subscriptions/{options.subscription_id}"
            f"/resourceGroups/{options.resource_group}"
            f"/providers/Microsoft.App/agents/{options.agent_name}"
            f"?api-version={API_VERSION}"
        )
        arm_token = self._credential.get_token(ARM_SCOPE)
        response = self._http.get(url, headers={"Authorization": f"Bearer {arm_token.token}"})

        if response.is_error:
            raise RuntimeError(f"ARM GET falló ({response.status_code}): {response.text}")

        endpoint = response.json().get("properties", {}).get("agentEndpoint")
        if not endpoint:
            raise RuntimeError("El agente aún no expone 'agentEndpoint'. ¿Está aprovisionado e iniciado?")

        self.endpoint = _normalize(endpoint)
        return self.endpoint

    def list_threads(self) -> Any:
        return self._request("GET", "/api/v1/threads")

    def create_thread(self, text: str) -> str:
        payload = self._request("POST", "/api/v1/threads", {"StartMessage": {"Text": text}})
        thread_id = _read_str(payload, "id", "threadId")
        if not thread_id:
            raise RuntimeError(f"No se pudo crear el thread: {payload}")
        return thread_id

    def send_message(self, thread_id: str, text: str) -> None:
        self._request("POST", f"/api/v1/threads/{thread_id}/messages", {"Text": text})

    def get_messages(self, thread_id: str) -> list[ChatMessage]:
        payload = self._request("GET", f"/api/v1/threads/{thread_id}/messages")
        return [msg for msg in (_parse_message(item) for item in _iter_collection(payload)) if msg]

    def get_approvals(self, thread_id: str) -> list[PendingApproval]:
        payload = self._request("GET", f"/api/v1/approvals/{thread_id}")
        approvals = []
        for item in _iter_collection(payload):
            approval_id = _read_str(item, "id", "approvalId", "requestId")
            if approval_id:
                summary = _read_str(item, "summary", "description", "title", "action") or "(sin descripción)"
                approvals.append(PendingApproval(approval_id, summary))
        return approvals

    def decide_approval(self, thread_id: str, approval_id: str, approve: bool) -> None:
        self._request(
            "POST",
            f"/api/v1/approvals/{thread_id}/{approval_id}/decision",
            {"decision": "Approved" if approve else "Rejected"},
        )

    def _request(self, method: str, path: str, json_body: dict | None = None) -> Any:
        response = self._http.request(
            method,
            self.endpoint + path,
            headers={"Authorization": f"Bearer {self._data_plane_token()}"},
            json=json_body,
        )
        if response.is_error:
            raise httpx.HTTPError(f"{method} {path} devolvió {response.status_code}: {response.text}")
        if not response.content:
            return {}
        try:
            return response.json()
        except ValueError:
            return {}

    def _data_plane_token(self) -> str:
        now = datetime.now(timezone.utc)
        if self._data_token and datetime.fromtimestamp(self._data_token.expires_on, timezone.utc) > now + timedelta(minutes=5):
            return self._data_token.token
        self._data_token = self._credential.get_token(DATA_PLANE_SCOPE)
        return self._data_token.token


# El esquema del data plane está en preview: se leen varias formas posibles de forma tolerante.
def _parse_message(item: Any) -> ChatMessage | None:
    if not isinstance(item, dict):
        return None
    content = _read_content(item)
    if not content or not content.strip():
        return None
    # El rol viene anidado en author.role: "User" o "SREAgent".
    role = _read_str(item.get("author"), "role") or _read_str(item, "role", "sender") or "assistant"
    return ChatMessage(
        role,
        content.strip(),
        _read_str(item, "id", "messageId"),
        bool(item.get("isComplete", True)),
    )


def _read_content(item: dict) -> str | None:
    for key in ("content", "text", "message", "body"):
        value = item.get(key)
        if isinstance(value, str):
            return value
        if isinstance(value, list):
            parts = [
                part if isinstance(part, str) else _read_str(part, "text", "content")
                for part in value
            ]
            joined = "\n".join(part for part in parts if part)
            if joined:
                return joined
        if isinstance(value, dict):
            nested = _read_str(value, "text", "content", "value")
            if nested:
                return nested
    return None


def _iter_collection(payload: Any) -> Iterable[Any]:
    if isinstance(payload, list):
        return payload
    if isinstance(payload, dict):
        for key in ("value", "messages", "items", "data", "approvals", "threads"):
            value = payload.get(key)
            if isinstance(value, list):
                return value
    return []


def _read_str(item: Any, *names: str) -> str | None:
    if not isinstance(item, dict):
        return None
    for name in names:
        value = item.get(name)
        if isinstance(value, str) and value:
            return value
    return None


def _normalize(endpoint: str) -> str:
    value = endpoint.strip().rstrip("/")
    return value if value.startswith("http") else f"https://{value}"