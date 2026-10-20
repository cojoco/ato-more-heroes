import UnityPy, sys, struct
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
G = sys.argv[1]; want = sys.argv[2].lower()
gen = TypeTreeGenerator("2022.3.62f2"); gen.load_local_dll_folder(G + "/Managed")
root = gen.get_nodes_up("Assembly-CSharp", "CardDataNew")
env = UnityPy.load(G + "/resources.assets")
raw = None
for o in env.objects:
    if o.type.name != "MonoBehaviour": continue
    try: n = o.peek_name()
    except Exception: continue
    if n and n.lower() == want: raw = o.get_raw_data(); break
pos = 0
def align():
    global pos; pos = (pos + 3) & ~3
def rd(node, path, depth):
    global pos
    t = node.m_Type; ch = node.m_Children or []
    prims = {"int":"<i","unsigned int":"<I","float":"<f","SInt64":"<q","UInt64":"<Q","double":"<d","SInt32":"<i","UInt32":"<I","SInt16":"<h","UInt16":"<H"}
    if path == "description.relatedCards":
        l = struct.unpack_from("<i", raw, pos)[0]; pos += 4; v = []
        for i in range(l):
            k = struct.unpack_from("<i", raw, pos)[0]; pos += 4; v.append(raw[pos:pos+k].decode()); pos += k; align()
        print(path, "=", v); return v
    if t in prims:
        v = struct.unpack_from(prims[t], raw, pos)[0]; pos += struct.calcsize(prims[t])
    elif t in ("bool","UInt8","SInt8","char"):
        v = raw[pos]; pos += 1
    elif t == "string":
        l = struct.unpack_from("<i", raw, pos)[0]; pos += 4; v = raw[pos:pos+l].decode("utf8","replace"); pos += l; align()
    elif ch and ch[0].m_Type == "Array":
        arr = ch[0]; l = struct.unpack_from("<i", raw, pos)[0]; pos += 4
        if l > 10000: raise Exception("bad len %d at %s" % (l, path))
        vals = []
        for i in range(l): vals.append(rd(arr.m_Children[1], path + "[%d]" % i, depth + 1))
        if (node.m_MetaFlag or 0) & 0x4000 or (arr.m_MetaFlag or 0) & 0x4000: align()
        v = vals
    else:
        v = {}
        for c in ch: v[c.m_Name] = rd(c, path + "." + c.m_Name, depth + 1)
    if (node.m_MetaFlag or 0) & 0x4000: align()
    if not isinstance(v, (dict, list)) and v not in (0, 0.0, "", None): print(path, "=", v)
    return v
try:
    for c in root.m_Children: rd(c, c.m_Name, 0)
except Exception as e: print("STOP", e, "pos", pos, "of", len(raw))
