import UnityPy, sys, os
d, name = sys.argv[1], sys.argv[2]
for f in sorted(os.listdir(d)):
    if not f.startswith("level") or "." in f: continue
    env = UnityPy.load(os.path.join(d, f))
    for o in env.objects:
        if o.type.name == "GameObject":
            try:
                if o.read_typetree()["m_Name"] == name: print(f); break
            except Exception: pass
