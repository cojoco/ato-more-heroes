# usage: one_event.py <Data dir> <eventId>  -> the event's description and every reply's non-empty fields
import UnityPy, sys
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
G, eid = sys.argv[1], sys.argv[2]
gen = TypeTreeGenerator("2022.3.62f2"); gen.load_local_dll_folder(G + "/Managed")
EN = gen.get_nodes_up("Assembly-CSharp", "EventData")
env = UnityPy.load(G + "/resources.assets")
for o in env.objects:
    if o.type.name != "MonoBehaviour": continue
    try:
        if o.peek_name() != eid: continue
    except Exception: continue
    tt = o.read_typetree(EN)
    print(tt.get("eventName"), "|", (tt.get("description") or "")[:300])
    for i, r in enumerate(tt["replys"]):
        keys = {k: v for k, v in r.items() if v not in (0, "", None, [], False) and not (isinstance(v, dict) and v.get("m_PathID") == 0)}
        print(f"--- reply {i}: {r.get('replyText')!r}")
        print("   ", {k: v for k, v in keys.items() if k not in ("replyText",) and not k.endswith("Text")})
