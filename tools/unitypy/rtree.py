# usage: rtree.py <levelN path> <depth> <root GameObject name>
# Transform tree with RectTransform fields (anchoredPosition, sizeDelta, anchors, pivot) and layout components.
import UnityPy, sys
path, maxdepth, root = sys.argv[1], int(sys.argv[2]), sys.argv[3]
env = UnityPy.load(path)
objs = {o.path_id: o for o in env.objects}
tr = {}; go = {}
for pid, o in objs.items():
    t = o.type.name
    if t in ("Transform", "RectTransform"): tr[pid] = (t, o.read_typetree())
    elif t == "GameObject": go[pid] = o.read_typetree()
def g_of(tp): return go[tr[tp][1]["m_GameObject"]["m_PathID"]]
def comps(g):
    out = []
    for c in g["m_Component"]:
        o = objs.get(c["component"]["m_PathID"])
        if o is None: continue
        n = o.type.name
        if n == "MonoBehaviour":
            try:
                mb = o.read(); n = mb.m_Script.read().m_ClassName
                if n in ("VerticalLayoutGroup","HorizontalLayoutGroup","GridLayoutGroup","ContentSizeFitter","LayoutElement"):
                    tt = o.read_typetree()
                    n += str({k: tt[k] for k in tt if k.startswith("m_") and k not in ("m_GameObject","m_Script","m_Enabled","m_EditorHideFlags","m_Name","m_EditorClassIdentifier")})
            except Exception: n = "MB"
        if n not in ("Transform", "RectTransform", "CanvasRenderer"): out.append(n)
    return out
def f(v): return "(%.1f,%.1f)" % (v["x"], v["y"])
def walk(tp, d):
    kind, t = tr[tp]; g = g_of(tp)
    s = "  " * d + g["m_Name"] + ("" if g["m_IsActive"] else " [inactive]")
    if kind == "RectTransform":
        s += " ap" + f(t["m_AnchoredPosition"]) + " size" + f(t["m_SizeDelta"]) + " amin" + f(t["m_AnchorMin"]) + " amax" + f(t["m_AnchorMax"]) + " piv" + f(t["m_Pivot"])
    else:
        s += " pos" + f(t["m_LocalPosition"])
    s += " sc" + f(t["m_LocalScale"]) + " " + " ".join(comps(g))
    print(s)
    if d < maxdepth:
        for ch in t["m_Children"]: walk(ch["m_PathID"], d + 1)
for pid, (k, t) in tr.items():
    if g_of(pid)["m_Name"] == root: walk(pid, 0)
