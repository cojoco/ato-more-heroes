import UnityPy, sys, re
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
G = sys.argv[1]; targets = set(sys.argv[2].split(","))
gen = TypeTreeGenerator("2022.3.62f2"); gen.load_local_dll_folder(G + "/Managed")
NN = gen.get_nodes_up("Assembly-CSharp", "NodeData")
EN = gen.get_nodes_up("Assembly-CSharp", "EventData")
ZN = gen.get_nodes_up("Assembly-CSharp", "ZoneData")
env = UnityPy.load(G + "/resources.assets")
objs = [o for o in env.objects if o.type.name == "MonoBehaviour"]
names = {}
for o in objs:
    try: names[o.path_id] = o.peek_name() or ""
    except Exception: names[o.path_id] = ""
byname = {v: k for k, v in names.items()}
tpid = {byname[t]: t for t in targets if t in byname}
print("target events found:", tpid)
byid = {o.path_id: o for o in objs}
# events whose replies chain to a target event (ssEvent/flEvent/...)
chain = {}
for o in objs:
    n = names[o.path_id]
    if not n.lower().startswith("e_"): continue
    try: tt = o.read_typetree(EN)
    except Exception: continue
    for r in tt.get("replys") or []:
        for k, v in r.items():
            if isinstance(v, dict) and v.get("m_PathID") in tpid and k.lower().endswith("event"):
                chain.setdefault(tpid[v["m_PathID"]], set()).add(f"{tt['eventId']} ('{tt.get('eventName')}') reply {k}")
print("chained from:", chain)
for o in objs:
    n = names[o.path_id]
    if n.lower().startswith("e_") or not re.match(r"^[a-z]+\d*_\d+[a-z]?$", n, re.I): continue
    try: tt = o.read_typetree(NN)
    except Exception: continue
    if "nodeEvent" not in tt: continue
    evs = [x.get("m_PathID") for x in tt.get("nodeEvent") or []] + [tt.get("replacementEvent", {}).get("m_PathID")]
    hits = [tpid[p] for p in evs if p in tpid]
    if hits:
        z = tt.get("nodeZone", {}); zn = "?"
        zo = byid.get(z.get("m_PathID"))
        if zo is not None:
            try: zt = zo.read_typetree(ZN); zn = f"{zt.get('zoneId')} '{zt.get('zoneName')}' sku='{zt.get('sku')}'"
            except Exception as e: zn = names.get(z.get("m_PathID"))
        print(f"node {tt.get('nodeId')} '{tt.get('nodeName')}' zone {zn} existsSku='{tt.get('existsSku')}' exists% {tt.get('existsPercent')} event% {tt.get('eventPercent')} events {hits}")
