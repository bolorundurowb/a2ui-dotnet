"""Replay a deterministic A2UI 1.0 form through the pinned Python Core and Agent SDKs."""

import argparse
import asyncio
import copy
import json
import subprocess
import uuid
from pathlib import Path

from a2ui.core import A2uiRpcError, CallOptions, Catalog, MessageProcessor, MessageProcessorOptions, NodeResolver
from a2ui.core.basic_catalog.v1_0 import BasicCatalog
from a2ui.core.catalog import AgentFunction, FunctionImplementation
from a2ui.core.schema.v1_0 import FunctionCall
from a2ui.inference_formats.direct_json.parser import DirectJsonParser
from a2ui.rpc import RendererFunctionCalls, RendererToAgentMessageProcessor


def model_turns(fixture, catalog):
    parser = DirectJsonParser([catalog])
    return {
        section: json.loads(parser.decompile(parser.compile(json.dumps(messages))))
        for section, messages in fixture.items()
    }


def rpc_catalog(definition):
    def echo(arguments, value):
        if arguments["fail"]:
            raise ValueError("Scripted appointment lookup failed.")
        return value

    def agent_echo(arguments):
        if arguments["fail"]:
            raise A2uiRpcError("Scripted appointment lookup failed.", code="EXECUTION_ERROR")
        return "Agent answered"

    return Catalog(
        catalog_id=definition["catalogId"],
        protocol_version=definition["protocolVersion"],
        functions=[
            FunctionImplementation(
                "echoRenderer", return_type="string",
                schema=definition["functions"]["echoRenderer"]["properties"]["args"],
                allowed_callers="agentOnly", execute=lambda args: echo(args, "Renderer answered"),
            ),
            AgentFunction(
                "echoAgent", return_type="string",
                schema=definition["functions"]["echoAgent"]["properties"]["args"],
                handler=agent_echo,
            ),
        ],
    )


def wire(message):
    return message.model_dump(by_alias=True, exclude_unset=True)


async def check_rpc(future, expected, fail):
    try:
        assert await asyncio.wait_for(future, timeout=5) == expected and not fail
    except A2uiRpcError as error:
        assert fail and error.code == "EXECUTION_ERROR", error


async def replay_rpc(processor, catalog, sent, surface_id, peer=None):
    calls = RendererFunctionCalls(catalogs=[catalog])
    inbound = RendererToAgentMessageProcessor([catalog], on_renderer_function_response=calls.resolve)
    trace = {"rendererCalls": [], "rendererReplies": [], "agentCalls": [], "agentReplies": []}

    def send(key, index, local):
        trace[key].append(local)
        return peer[key][index] if peer is not None else local

    for index, fail in enumerate((False, True)):
        arguments = {"fail": fail}
        call, future = calls.call(
            catalog_id=catalog.id, name="echoRenderer", args=arguments, surface_id=surface_id,
            function_call_id=f"renderer-{index}",
        )
        (reply,) = await processor.process_messages_async([send("rendererCalls", index, wire(call))])
        received = await inbound.process_message(send("rendererReplies", index, reply))
        assert received.handled
        await check_rpc(future, "Renderer answered", fail)

        future = processor.call_agent_function(
            surface_id, FunctionCall(**{"@call": "echoAgent", "catalogId": catalog.id, "args": arguments}),
            CallOptions(function_call_id=f"agent-{index}", version="1.0"),
        )
        result = await inbound.process_message(send("agentCalls", index, sent.pop(0)))
        (reply,) = result.replies
        processor.process_messages([send("agentReplies", index, wire(reply))])
        await check_rpc(future, "Agent answered", fail)

    calls.dispose()
    return trace


async def render(turns, catalog, rpc, peer=None):
    actions = []
    sent = []

    def on_outbound(message):
        if "callAgentFunction" in message:
            sent.append(message)

    processor = MessageProcessor(
        [catalog, rpc], action_handler=actions.append,
        options=MessageProcessorOptions(outbound_listener=on_outbound),
    )
    opening = turns["opening"]
    processor.process_messages(opening[:1])
    surface_id = opening[0]["createSurface"]["surfaceId"]
    surface = processor.model.get_surface(surface_id)
    resolver = NodeResolver(surface)

    def node(component_id):
        return next(child for child in resolver.root.props["children"] if child.component_id == component_id)

    def slots():
        return [child.props["text"].value for child in node("slots").props["children"]]

    initial_slots = slots()
    assert initial_slots == ["Morning", "Afternoon"]
    processor.process_messages(opening[1:2])
    assert not node("name").is_valid and node("name").validation_errors == ["Enter a name"]
    processor.process_messages(opening[2:])
    reordered_slots = slots()
    assert reordered_slots == ["Afternoon", "Morning"]
    node("name").props["value"].set("Ada")
    assert node("name").is_valid and node("name").props["value"].value == "Ada"
    node("submit").props["action"]()
    (action,) = actions
    action = {"version": "1.0", "action": action}
    assert action["action"]["context"] == {"name": "Ada"}
    accepted = []
    inbound = RendererToAgentMessageProcessor([catalog], on_action=lambda message: accepted.append(message) or True)
    dispatch = await inbound.process_message(peer["action"] if peer is not None else action)
    assert dispatch.handled and len(accepted) == 1
    renderer_data = processor.get_renderer_data_model("1.0")
    assert renderer_data["surfaces"][surface_id]["name"] == "Ada"
    rpc_trace = await replay_rpc(processor, rpc, sent, surface_id, peer["rpc"] if peer is not None else None)

    confirmation = turns["confirmation"]
    processor.process_messages(confirmation[:1])
    confirmed = node("label").props["text"].value
    assert confirmed == "Appointment confirmed"
    processor.process_messages(confirmation[1:])
    disposed = processor.model.get_surface(surface_id) is None and surface.disposed and resolver.root is None
    assert disposed and processor.get_renderer_data_model("1.0") is None
    resolver.dispose()
    processor.dispose()
    return {
        "initialSlots": initial_slots,
        "reorderedSlots": reordered_slots,
        "rendererData": renderer_data,
        "action": action,
        "rpc": rpc_trace,
        "confirmed": confirmed,
        "disposed": disposed,
    }


def comparable(trace):
    trace = copy.deepcopy(trace)
    # Each renderer timestamps its own action. The remaining action fields must match.
    del trace["action"]["action"]["timestamp"]
    for key in ("rendererReplies", "agentReplies"):
        for reply in trace["rpc"][key]:
            for payload in (value for value in reply.values() if isinstance(value, dict)):
                if "error" in payload:
                    del payload["error"]["message"]
    return trace


async def main():
    arguments = argparse.ArgumentParser(description=__doc__)
    arguments.add_argument("consumer", type=Path)
    arguments.add_argument("fixture", type=Path)
    arguments.add_argument("output", type=Path)
    args = arguments.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    catalog = BasicCatalog()
    rpc = rpc_catalog(json.loads(args.fixture.with_name("rpc-catalog.json").read_text(encoding="utf-8")))
    surface_id = f"appointment-{uuid.uuid4().hex}"
    source = args.fixture.read_text(encoding="utf-8").replace("APPOINTMENT_SURFACE_ID", surface_id)
    python_turns = model_turns(json.loads(source), catalog)
    python_rendered = await render(python_turns, catalog, rpc)
    python_path = args.output / "python.json"
    python_trace = python_turns | python_rendered
    python_path.write_text(json.dumps(python_trace, indent=2), encoding="utf-8")
    dotnet_path = args.output / "dotnet.json"
    subprocess.run(["dotnet", str(args.consumer), str(args.fixture), str(dotnet_path), str(python_path)], check=True)
    dotnet_trace = json.loads(dotnet_path.read_text(encoding="utf-8"))
    dotnet_turns = {section: dotnet_trace[section] for section in ("opening", "confirmation")}
    python_cross = await render(dotnet_turns, catalog, rpc, dotnet_trace)
    assert comparable(dotnet_trace) == comparable(dotnet_turns | python_cross), "Cross-renderer transcript differs"
    assert comparable(dotnet_trace) == comparable(python_trace), "Agent wire messages and renderer outcomes differ"
    print("Both Python and .NET replayed the peer's A2UI 1.0 appointment form successfully.")


if __name__ == "__main__":
    asyncio.run(main())
