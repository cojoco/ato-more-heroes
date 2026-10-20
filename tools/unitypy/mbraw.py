# usage: mbraw.py <Data dir> <levelN> <GameObject name,...>
# Script class names of the MonoBehaviours on those GameObjects, read from raw bytes (for scenes whose MonoBehaviours have no
# typetree), plus the first bytes of their serialized fields as hex (e.g. a GridLayoutGroup's padding / cell size).
import UnityPy, sys, os, struct
D, level = sys.argv[1], sys.argv[2]
env = UnityPy.load(os.path.join(D, level))
lvf = None
L = {}
for o in env.objects:
    L[o.path_id] = o; lvf = o.assets_file
ext = [e.path for e in lvf.externals]
scripts_cache = {}
def script_name(fid, pid):
    path = ext[fid - 1]
    fn = os.path.join(D, os.path.basename(path))
    if fn not in scripts_cache:
        e2 = UnityPy.load(fn); scripts_cache[fn] = {o.path_id: o for o in e2.objects}
    so = scripts_cache[fn].get(pid)
    return so.read().m_ClassName if so else f"?{path}:{pid}"
names = set(sys.argv[3].split(","))
for o in list(L.values()):
    if o.type.name != "GameObject": continue
    g = o.read_typetree()
    if g["m_Name"] not in names: continue
    out = []
    for c in g["m_Component"]:
        co = L.get(c["component"]["m_PathID"])
        if co is None or co.type.name != "MonoBehaviour": continue
        raw = co.get_raw_data()
        fid, pid = struct.unpack_from("<iq", raw, 16)
        try: n = script_name(fid, pid)
        except Exception as e: n = f"?{fid}:{pid} {e}"
        out.append((n, raw[32:].hex()))
    print(g["m_Name"], [(n, h[:120]) for n, h in out])
