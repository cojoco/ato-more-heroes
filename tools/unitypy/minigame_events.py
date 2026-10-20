import UnityPy, sys, json
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
G = sys.argv[1]; out = sys.argv[2]
gen = TypeTreeGenerator("2022.3.62f2"); gen.load_local_dll_folder(G + "/Managed")
NODES = gen.get_nodes_up("Assembly-CSharp", "EventData")
env = UnityPy.load(G + "/resources.assets")
byid = {o.path_id: o for o in env.objects}
def name_of(pp):
    if not isinstance(pp, dict) or pp.get("m_PathID", 0) == 0: return None
    o = byid.get(pp["m_PathID"]) if pp.get("m_FileID", 0) == 0 else None
    if o is None: return f"ext:{pp['m_FileID']}:{pp['m_PathID']}"
    try: return o.peek_name() or o.read().m_Name
    except Exception: return f"obj:{pp['m_PathID']}"
res = []; events = {}
for o in env.objects:
    if o.type.name != "MonoBehaviour": continue
    try:
        nm = o.peek_name() or ""
    except Exception: nm = ""
    if not nm.lower().startswith("e_"): continue
    try: tt = o.read_typetree(NODES)
    except Exception: continue
    if not isinstance(tt, dict) or "replys" not in tt or "eventId" not in tt: continue
    events[tt["eventId"]] = tt
    for ri, r in enumerate(tt["replys"] or []):
        for pre, what in (("ss","success"),("fl","fail"),("ssc","crit success"),("flc","crit fail")):
            for game in ("CardPlayerPairsGame","CardPlayerGame"):
                if r.get(pre + game):
                    res.append(dict(event=tt["eventId"], name=tt.get("eventName"), reply=ri, outcome=what, game=game,
                                    pack=name_of(r.get(pre + game + "PackData")), replyText=r.get("replyText") or r.get("replyActionText"),
                                    roll=r.get("ssRoll"), tier=tt.get("eventTier"), history=tt.get("historyMode")))
json.dump(res, open(out, "w"), indent=1)
print(len(events), "events;", len(res), "minigame replies")
for x in res: print(x)
