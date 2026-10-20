# usage: scenes.py <Data dir>  -> scene number and path from BuildSettings (levelN = scene N)
import UnityPy, sys
env = UnityPy.load(sys.argv[1] + "/globalgamemanagers")
for o in env.objects:
    if o.type.name == "BuildSettings":
        for i, s in enumerate(o.read_typetree()["scenes"]): print(i, s)
