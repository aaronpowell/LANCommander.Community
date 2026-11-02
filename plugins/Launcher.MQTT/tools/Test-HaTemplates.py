"""Representative HA sandbox test; not an integration test against a running HA."""

import argparse
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import random
from uuid import UUID

from jinja2 import StrictUndefined, pass_context
from jinja2.sandbox import ImmutableSandboxedEnvironment
import yaml


@pass_context
def random_every_time(context, values):
    # HA's random filter is context-dependent, preventing Jinja constant folding.
    # https://github.com/home-assistant/core/blob/2026.10.4/homeassistant/helpers/template/extensions/functional.py
    return random.choice(values)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("discovery_json", type=Path)
    parser.add_argument("--script", type=Path, default=Path("examples/home-assistant-launch-game.yaml"))
    args = parser.parse_args()
    discovery = json.loads(args.discovery_json.read_text(encoding="utf-8-sig"))
    script = yaml.safe_load(args.script.read_text(encoding="utf-8-sig"))
    env = ImmutableSandboxedEnvironment(undefined=StrictUndefined)
    env.filters["random"] = random_every_time
    env.filters["to_json"] = json.dumps
    fixed_now = datetime(2026, 10, 30, 9, 0, tzinfo=timezone(timedelta(hours=11)))
    calls = 0

    def now():
        nonlocal calls
        # Deliberately move time forward on every call to catch separate now()
        # evaluations that make expiresAt - issuedAt exceed the 30s protocol cap.
        value = fixed_now + timedelta(microseconds=calls)
        calls += 1
        return value

    env.globals.update(now=now, timedelta=timedelta, as_datetime=datetime.fromisoformat)
    assert "uuid" not in env.globals
    if isinstance(discovery, dict):
        discovery = [discovery]
    legacy = next(entity for entity in discovery if "/command/" in entity.get("command_topic", "")
                  and entity["command_topic"].split("/")[-1] not in ("select", "launch_selected", "install_selected"))
    command_template = env.from_string(legacy["command_template"])
    ids = set()

    def check(payload, game_id, action="launch"):
        assert set(payload) == {"requestId", "action", "gameId", "issuedAt", "expiresAt"}
        request = UUID(payload["requestId"])
        assert str(request) == payload["requestId"] and request.version == 4
        assert request not in ids, "Distinct renders must not reuse a deduplication key"
        ids.add(request)
        assert payload["action"] == action and payload["gameId"] == game_id
        issued = datetime.fromisoformat(payload["issuedAt"])
        expires = datetime.fromisoformat(payload["expiresAt"])
        assert issued.utcoffset() is not None
        assert expires - issued == timedelta(seconds=30)
        assert len(json.dumps(payload).encode()) <= 1024

    game_id = legacy["command_topic"].split("/")[-1]
    for _ in range(10000):
        check(json.loads(command_template.render(value="PRESS")), game_id)
    assert calls == 10000, "Button should capture now() exactly once per press"

    variables = next(step["variables"] for step in script["sequence"] if "variables" in step)
    publish = next(step["data"] for step in script["sequence"] if step.get("action") == "mqtt.publish")
    assert "payload_template" not in publish
    request_template = env.from_string(variables["request_id"])
    issued_template = env.from_string(variables["issued_at"])
    payload_template = env.from_string(publish["payload"])
    for _ in range(1000):
        values = {
            "request_id": request_template.render().strip(),
            "issued_at": issued_template.render().strip(),
        }
        rendered = json.loads(payload_template.render(**values))
        assert rendered["requestId"] == values["request_id"]
        check(rendered, publish["topic"].split("/")[-1])

    # All random bits must vary even at the exact same timestamp.
    env.globals["now"] = lambda: fixed_now
    for _ in range(1000):
        check(json.loads(command_template.render(value="PRESS")), game_id)

    for entity in discovery:
        if "command_template" not in entity or entity is legacy:
            continue
        action = entity["command_topic"].split("/")[-1]
        assert action in ("select", "launch_selected", "install_selected")
        template = env.from_string(entity["command_template"])
        if action == "select":
            assert not entity["optimistic"] and not entity["retain"]
            options = entity["options"]
            assert options and len(options) == len(set(options))
            for option in options:
                # HA MqttSelect.async_select_option passes the option as `value`
                # through MqttCommandTemplate; no plugin-specific template globals.
                for _ in range(1000):
                    payload = json.loads(template.render(value=option))
                    check(payload, option[-37:-1], action)
                    UUID(payload["gameId"])
        else:
            for _ in range(1000):
                check(json.loads(template.render(value="PRESS")), str(UUID(int=0)), action)
    print(f"Validated {len(ids):,} unique UUIDv4 request IDs, exact 30-second lifetimes, "
          "JSON wire shape, repeated/constant-time renders, sample-script correlation, "
          "and emitted select/install/run templates when present.")


if __name__ == "__main__":
    main()
