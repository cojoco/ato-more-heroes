import re,sys
# usage: count.py il.txt startline endline  -> per method counts of ldc.i4 3/4/5/8 (class::method)
lines=open(sys.argv[1],encoding='utf-8',errors='replace').read().split('\n')
a,b=int(sys.argv[2]),int(sys.argv[3])
cls=None;meth=None;cnt={};pending=False
out=[]
def flush():
    if meth and cnt: out.append(f"{cls} :: {meth}: "+", ".join(f"{k}x{v}" for k,v in sorted(cnt.items())))
for l in lines[a:b]:
    m=re.match(r'\s*\.class .* (\S+)$',l)
    if m: cls=m.group(1)
    if re.match(r'\s*\.method',l):
        flush(); cnt={}; pending=True
    if pending:
        m=re.search(r"([\w<>'.]+)\s*\(",l)
        if m: meth=m.group(1); pending=False
        continue
    m=re.search(r'\bldc\.i4(?:\.s)?\s+(-?\d+)|\bldc\.i4\.(\d|m1)\b',l)
    if m:
        v=m.group(1) or m.group(2)
        v=-1 if v=='m1' else int(v)
        if v in (3,4,5,8): cnt[v]=cnt.get(v,0)+1
flush()
print("\n".join(out))
