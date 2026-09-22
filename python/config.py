import os
from dataclasses import dataclass

from dotenv import load_dotenv

ARM_SCOPE = "https://management.azure.com/.default"
DATA_PLANE_SCOPE = "https://azuresre.dev/.default"
API_VERSION = "2025-05-01-preview"


@dataclass
class AgentOptions:
    subscription_id: str | None = None
    resource_group: str | None = None
    agent_name: str | None = None
    agent_endpoint: str | None = None
    poll_interval_seconds: float = 3.0
    response_timeout_seconds: float = 300.0

    @classmethod
    def from_env(cls) -> "AgentOptions":
        load_dotenv()
        return cls(
            subscription_id=os.getenv("SREAGENT_SUBSCRIPTION_ID"),
            resource_group=os.getenv("SREAGENT_RESOURCE_GROUP"),
            agent_name=os.getenv("SREAGENT_AGENT_NAME"),
            agent_endpoint=os.getenv("SREAGENT_AGENT_ENDPOINT"),
            poll_interval_seconds=float(os.getenv("SREAGENT_POLL_INTERVAL_SECONDS", "3")),
            response_timeout_seconds=float(os.getenv("SREAGENT_RESPONSE_TIMEOUT_SECONDS", "300")),
        )

    def validate(self) -> None:
        if self.agent_endpoint:
            return
        missing = [
            name
            for name, value in (
                ("SREAGENT_SUBSCRIPTION_ID", self.subscription_id),
                ("SREAGENT_RESOURCE_GROUP", self.resource_group),
                ("SREAGENT_AGENT_NAME", self.agent_name),
            )
            if not value
        ]
        if missing:
            raise ValueError(
                "Define SREAGENT_AGENT_ENDPOINT o bien: " + ", ".join(missing)
            )