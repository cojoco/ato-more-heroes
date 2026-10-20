# usage: sprite_of.py <Data dir> <levelN> <GameObject name> <out dir>
# Saves as PNG every Sprite referenced by the MonoBehaviours (e.g. a uGUI Image) of GameObjects with that name in that scene.
import UnityPy, sys, os, struct
D, level, goname, out = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
env = UnityPy.load(os.path.join(D, level))
L = {o.path_id: o for o in env.objects}
lvf = next(iter(L.values())).assets_file
for o in L.values():
    if o.type.name != "GameObject": continue
    g = o.read_typetree()
    if g["m_Name"] != goname: continue
    for c in g["m_Component"]:
        co = L.get(c["component"]["m_PathID"])
        if co is None or co.type.name != "MonoBehaviour": continue
        raw = co.get_raw_data()
        # Image: after base (32 + name len) ... just scan for PPtrs to Sprite objects
        for off in range(32, len(raw) - 12, 4):
            fid, pid = struct.unpack_from("<iq", raw, off)
            if fid == 0 and pid in L and L[pid].type.name == "Sprite":
                sp = L[pid].read(); print(g["m_Name"], "sprite", sp.m_Name); sp.image.save(os.path.join(out, sp.m_Name + ".png"))
            elif 0 < fid <= len(lvf.externals) and pid > 0:
                try:
                    e2 = UnityPy.load(os.path.join(D, os.path.basename(lvf.externals[fid-1].path)))
                    for x in e2.objects:
                        if x.path_id == pid and x.type.name == "Sprite":
                            sp = x.read(); print(g["m_Name"], "sprite", sp.m_Name); sp.image.save(os.path.join(out, sp.m_Name + ".png"))
                except Exception as e: pass
