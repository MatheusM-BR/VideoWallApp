import re,json,sys
src=open(sys.argv[1],encoding='utf-8').read()
src=src[src.index('Sections { get; } ='):]
src=src[src.index('['):]
toks=[];i=0
while i<len(src):
    c=src[i]
    if c.isspace(): i+=1; continue
    if src.startswith('//',i): i=src.index('\n',i); continue
    if c=='"':
        j=i+1;s=''
        while src[j]!='"':
            if src[j]=='\\':
                n=src[j+1]; s+={'n':'\n','t':'\t'}.get(n,n); j+=2
            else: s+=src[j]; j+=1
        toks.append(('s',s)); i=j+1; continue
    m=re.match(r'[A-Za-z_]\w*',src[i:])
    if m: toks.append(('id',m.group())); i+=len(m.group()); continue
    toks.append(('p',c)); i+=1
pos=0
def peek(): return toks[pos]
def take(): 
    global pos; t=toks[pos]; pos+=1; return t
def expr():
    t=take()
    if t[0]=='s': return t[1]
    if t==('id','new'):
        name='Section'
        if peek()[0]=='id': name=take()[1]
        assert take()==('p','(')
        args=arglist(')')
        return {'type':name,'args':args}
    if t==('p','('):
        return tuple(arglist(')'))
    raise Exception(t)
def arglist(end):
    out=[]
    while peek()!=('p',end):
        out.append(expr())
        if peek()==('p',','): take()
    take(); return out
assert take()==('p','[')
secs=arglist(']')
json.dump(secs,open(sys.argv[2],'w'),ensure_ascii=False,indent=1)
print(len(secs),[s['args'][0] for s in secs])
