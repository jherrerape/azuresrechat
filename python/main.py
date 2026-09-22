import sys
import time

import httpx
from azure.identity import DefaultAzureCredential

from client import SreAgentClient
from config import AgentOptions

CYAN, GREEN, RED, RESET = "\033[96m", "\033[92m", "\033[91m", "\033[0m"


def main() -> int:
    options = AgentOptions.from_env()
    try:
        options.validate()
    except ValueError as exc:
        print(f"{RED}Error de configuración: {exc}{RESET}", file=sys.stderr)
        return 1

    credential = DefaultAzureCredential(exclude_interactive_browser_credential=False)

    with SreAgentClient(credential) as client:
        try:
            print("Resolviendo endpoint del agente...")
            print(f"Endpoint: {client.resolve_endpoint(options)}")
        except Exception as exc:
            print(f"{RED}Error: {exc}{RESET}", file=sys.stderr)
            return 1

        state = {"thread_id": None, "seen": set(), "printed": 0}
        print("Comandos: /nuevo  /hilos  /abrir <id>  /aprobaciones  /aprobar <id>  /rechazar <id>  /salir")
        print("-" * 70)

        while True:
            try:
                user_input = input(f"\n{CYAN}Tú> {RESET}").strip()
            except (EOFError, KeyboardInterrupt):
                print()
                return 0

            if not user_input:
                continue

            if user_input.startswith("/"):
                if handle_command(client, state, user_input):
                    continue
                return 0

            try:
                if state["thread_id"] is None:
                    state["thread_id"] = client.create_thread(user_input)
                    print(f"Thread: {state['thread_id']}")
                else:
                    client.send_message(state["thread_id"], user_input)
                stream_response(client, state, options)
            except KeyboardInterrupt:
                print("\n(interrumpido)")
            except Exception as exc:
                print(f"\n{RED}Error: {exc}{RESET}", file=sys.stderr)

    return 0


def handle_command(client: SreAgentClient, state: dict, raw: str) -> bool:
    """Devuelve False solo cuando el usuario pide salir."""
    parts = raw.split(maxsplit=1)
    command = parts[0].lower()
    argument = parts[1].strip() if len(parts) > 1 else None

    try:
        if command in ("/salir", "/exit"):
            return False

        if command == "/nuevo":
            state.update(thread_id=None, seen=set(), printed=0)
            print("Nueva conversación: se creará al enviar el próximo mensaje.")

        elif command == "/hilos":
            for thread in client.list_threads().get("value", []):
                print(f"  [{thread.get('id')}] {thread.get('title')}")

        elif command == "/abrir":
            if not argument:
                print("Indica el id del thread.")
            else:
                state.update(thread_id=argument, seen=set(), printed=0)
                for message in client.get_messages(argument):
                    print(f"  {message.role}: {message.content[:120]}")

        elif command == "/aprobaciones":
            if state["thread_id"] is None:
                print("Aún no hay conversación activa.")
            else:
                approvals = client.get_approvals(state["thread_id"])
                if not approvals:
                    print("No hay aprobaciones pendientes.")
                for approval in approvals:
                    print(f"  [{approval.approval_id}] {approval.summary}")

        elif command in ("/aprobar", "/rechazar"):
            if state["thread_id"] is None or not argument:
                print("Necesitas una conversación activa y el id de la aprobación.")
            else:
                client.decide_approval(state["thread_id"], argument, command == "/aprobar")
                print("Decisión enviada.")

        else:
            print(f"Comando desconocido: {command}")

    except Exception as exc:
        print(f"{RED}Error: {exc}{RESET}", file=sys.stderr)

    return True


def stream_response(client: SreAgentClient, state: dict, options: AgentOptions) -> None:
    deadline = time.monotonic() + options.response_timeout_seconds
    got_reply = False

    print("Agente está pensando", end="", flush=True)

    while time.monotonic() < deadline:
        time.sleep(max(1.0, options.poll_interval_seconds))

        try:
            messages = client.get_messages(state["thread_id"])
        except httpx.HTTPError:
            print(".", end="", flush=True)
            continue

        new_messages = messages[state["printed"]:]
        state["printed"] = len(messages)
        turn_done = False

        for message in new_messages:
            key = message.message_id or f"{message.role}:{hash(message.content)}"
            if key in state["seen"] or "user" in message.role.lower():
                continue
            state["seen"].add(key)

            print(f"\n\n{GREEN}Agente> {message.content}{RESET}")
            got_reply = True
            turn_done = message.is_complete

        if turn_done:
            return

        if not got_reply:
            print(".", end="", flush=True)

    if not got_reply:
        print("\n(sin respuesta dentro del tiempo de espera)")


if __name__ == "__main__":
    raise SystemExit(main())