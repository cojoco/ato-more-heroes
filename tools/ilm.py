import re,sys
# usage: ilm.py il.txt Class Method [const...]  -> constants (all or given) with the next instruction, for each matching method
lines=[x.rstrip() for x in open(sys.argv[1],encoding="utf-8",errors="replace").read().split("\n")]
cls,meth=sys.argv[2],sys.argv[3]; want=set(int(x) for x in sys.argv[4:])
top=None; inner=None; i=0
def const(l):
    m=re.search(r'\bldc\.i4(?:\.s)?\s+(-?\d+)\s*$|\bldc\.i4\.(\d|m1)\s*$',l)
    if not m: return None
    v=m.group(1) or m.group(2); return -1 if v=='m1' else int(v)
while i<len(lines):
    l=lines[i]
    m=re.match(r'\.class .*? (\S+)$',l)
    if m: top=m.group(1); inner=None
    m=re.match(r'\s+\.class .*? (\S+)$',l)
    if m: inner=m.group(1)
    if re.match(r'\s+} // end of class',l): inner=None
    if top==cls and re.match(r'\s*\.method',l):
        j=i; head=''
        while '(' not in lines[j]: j+=1
        name=re.search(r"([\w<>'.|]+)\s*\(",lines[j]).group(1)
        target = (name==meth and (inner is None or not inner.startswith("'<"))) or (name=='MoveNext' and inner and inner.startswith(f"'<{meth}>"))
        if target:
            print(f"== {cls}::{inner+'::' if inner else ''}{name} (line {i+1})")
            k=j; depth=0; started=False; counts={}
            while k<len(lines):
                if '{' in lines[k]: depth+=lines[k].count('{'); started=True
                if '}' in lines[k]: depth-=lines[k].count('}')
                c=const(lines[k])
                if c is not None and (not want or c in want):
                    counts[c]=counts.get(c,0)+1
                    nxt=lines[k+1].strip()
                    print(f"  {c} #{counts[c]-1}: {lines[k].strip()[:40]:<40} -> {nxt[:70]}")
                if started and depth==0: break
                k+=1
            print("  counts:",counts)
            i=k
    i+=1
