import UnityPy, sys
path, maxdepth = sys.argv[1], int(sys.argv[2])
filt = sys.argv[3] if len(sys.argv) > 3 else None
env = UnityPy.load(path)
objs = {o.path_id: o for o in env.objects}
tr = {}; go = {}
for pid, o in objs.items():
    t = o.type.name
    if t in ("Transform", "RectTransform"): tr[pid] = o.read_typetree()
    elif t == "GameObject": go[pid] = o.read_typetree()
def gname(tp): return go[tr[tp]["m_GameObject"]["m_PathID"]]
def comps(tp):
    g = gname(tp); out = []
    for c in g["m_Component"]:
        o = objs.get(c["component"]["m_PathID"])
        if o is None: continue
        n = o.type.name
        if n == "MonoBehaviour":
            try: n = "MB:" + o.read().m_Script.read().m_ClassName
            except Exception: n = "MB"
        if n not in ("Transform", "RectTransform"): out.append(n)
    return out, g["m_IsActive"]
def v(x): return "(%.2f,%.2f)" % (x["x"], x["y"])
def walk(tp, d, show):
    g = gname(tp); name = g["m_Name"]
    if filt and not show and name == filt: show = True; d0[0] = d
    if show and d - d0[0] <= maxdepth:
        c, act = comps(tp); t = tr[tp]
        print("  " * (d - d0[0]) + name + ("" if act else " [inactive]") + " pos" + v(t["m_LocalPosition"]) + " scale" + v(t["m_LocalScale"]) + " " + " ".join(c))
    for ch in tr[tp]["m_Children"]:
        walk(ch["m_PathID"], d + 1, show)
d0 = [0]
for p, t in tr.items():
    if t["m_Father"]["m_PathID"] == 0: walk(p, 0, filt is None)
