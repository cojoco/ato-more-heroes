import json, sys
d = json.load(open(sys.argv[1]))
low = {k.lower(): k for k in d}

def nondef(x, p=""):
    out = {}
    if isinstance(x, dict):
        for k, v in x.items():
            out.update(nondef(v, p + k + "."))
    elif isinstance(x, list):
        if x:
            out[p[:-1]] = x
    elif x not in (0, 0.0, None, "", False):
        out[p[:-1]] = x
    return out

for n in sys.argv[2:]:
    k = low.get(n.lower())
    if not k:
        print(n, "MISSING")
        continue
    print(n, nondef(d[k].get("cardManagement", {})), "target", nondef(d[k].get("targetData", {})), "type", d[k].get("cardType"))
