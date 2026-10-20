# usage: findname.py <Data dir> <text>: GameObjects whose name contains <text> in sharedassets*.assets and resources.assets
# (prefabs, not scenes; use findgo.py for scenes).
import UnityPy, sys, os, glob
D, pat = sys.argv[1], sys.argv[2].lower()
files = sorted(glob.glob(os.path.join(D, "sharedassets*.assets")) + glob.glob(os.path.join(D, "resources.assets")))
for f in files:
    try:
        env = UnityPy.load(f)
    except Exception as e:
        print("ERR", f, e); continue
    for o in env.objects:
        if o.type.name == "GameObject":
            try:
                n = o.read().m_Name
            except Exception:
                continue
            if pat in n.lower():
                print(os.path.basename(f), o.path_id, n)
