import subprocess,json,time,re,sys
P='samples/SamplingAliasProbe/bin/Release/net10.0/SamplingAliasProbe.dll'
CLI='src/DotnetDiagnostics.Cli/bin/Release/net10.0/dotnet-diagnostics.dll'
out=open('scratch1078/results.jsonl','a')
chunks=[float(x) for x in sys.argv[1].split(',')]; reps=int(sys.argv[2])
for rep in range(reps):
  for c in chunks:
    p=subprocess.Popen(['dotnet',P,str(c),'19'],stdout=subprocess.PIPE,text=True)
    line=p.stdout.readline(); pid=re.search(r'pid=(\d+)',line).group(1)
    r=subprocess.run(['dotnet',CLI,'collect','--kind','cpu','--pid',pid,'--duration','12','--top','100','--json','--no-resolve-source-lines'],capture_output=True,text=True)
    tr=p.stdout.read(); p.wait()
    try: d=json.loads(r.stdout)['data']
    except Exception as e: print('fail',c,r.stderr[:200]); continue
    inc={}
    for h in d['topHotspots']:
        m=h['frame']['method']
        for k in 'ABM':
            if f'Path{k}|' in m: inc[k]=h['inclusiveSamples']
    t=re.search(r'A=([\d.]+) B=([\d.]+) M=([\d.]+)',tr)
    rec=dict(chunkMs=c,rep=rep,total=d['totalSamples'],A=inc.get('A',0),B=inc.get('B',0),M=inc.get('M',0),tA=float(t[1]),tB=float(t[2]),tM=float(t[3]))
    out.write(json.dumps(rec)+'\n'); out.flush(); print(rec,flush=True)
