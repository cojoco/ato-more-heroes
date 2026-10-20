import UnityPy, sys, json
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
G = sys.argv[1]; out = sys.argv[2]
gen = TypeTreeGenerator("2022.3.62f2"); gen.load_local_dll_folder(G + "/Managed")
NODES = gen.get_nodes_up("Assembly-CSharp", "CardDataNew")
env = UnityPy.load(G + "/resources.assets")
res = {}
for o in env.objects:
    if o.type.name != "MonoBehaviour": continue
    try: tt = o.read_typetree(NODES)
    except Exception: continue
    if not isinstance(tt, dict) or "cardManagement" not in tt: continue
    def clean(x):
        if isinstance(x, dict):
            if set(x.keys()) == {"m_FileID", "m_PathID"}: return None if x["m_PathID"] == 0 else "ref"
            return {k: clean(v) for k, v in x.items()}
        if isinstance(x, list): return [clean(v) for v in x]
        return x
    res[str(tt.get("m_Name"))] = clean({k: tt[k] for k in tt if k != "m_Script"})
json.dump(res, open(out, "w"))
print(len(res))
