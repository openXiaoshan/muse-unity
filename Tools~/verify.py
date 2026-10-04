#!/usr/bin/env python3
"""Install this package into a disposable Unity project and run isolated acceptance.
No personal credentials, recording or live Muse/ASR service is used.
"""
import argparse, os, shutil, subprocess
from pathlib import Path
root = Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser()
p.add_argument('--unity',type=Path,required=True,help='Unity Editor executable (Unity 6)')
p.add_argument('--sdk',type=Path,required=True,help='Pinned Meta SDK checkout; see DEVELOPMENT.md')
p.add_argument('--python',type=Path,required=True,help='Python with test requirements installed')
p.add_argument('--output',type=Path,required=True,help='Disposable project outside this package')
a=p.parse_args(); target=a.output.resolve()
if target == root or root in target.parents: raise SystemExit('Use a disposable directory outside the package')
target.mkdir(parents=True,exist_ok=True)
env=dict(os.environ,MUSE_UNITY_PACKAGE=str(root),MUSE_GADGET_SDK=str(a.sdk.resolve()))
for name in ['ELEVENLABS_API_KEY','MUSE_SDK_TOKEN']: env.pop(name,None)
def run(method, log, extra=()):
    args=[str(a.unity.resolve()),'-batchmode','-projectPath',str(target),'-logFile',str(target/log),*extra]
    if method: args+=['-executeMethod',method]
    r=subprocess.run(args,env=env,timeout=600)
    if r.returncode: raise SystemExit(f'Unity failed ({r.returncode}); see {target/log}')
if not (target/'ProjectSettings/ProjectVersion.txt').exists():
    run(None,'create.log',['-createProject',str(target),'-quit'])
editor=target/'Assets/Editor';editor.mkdir(parents=True,exist_ok=True)
shutil.copyfile(root/'Tools~/PackageBootstrap.cs',editor/'PackageBootstrap.cs')
# Test harness cannot compile until the package is installed.
for name in ['VerifyMuseObject.cs','VerifyMuseObject.cs.meta']:
    (editor/name).unlink(missing_ok=True)
run('PackageBootstrap.Install','install.log')
run('MuseObjectTools.PrepareTests','prepare.log')
for name in ['noise-vector.json','pairing-vector.json']: shutil.copyfile(root/'Tools~'/name,target/name)
shutil.copyfile(root/'Tools~/VerifyMuseObject.cs',editor/'VerifyMuseObject.cs')
for name in ['report.txt','edit-report.txt']: (target/name).unlink(missing_ok=True)
with (target/'peer.log').open('w') as errors:
    peer=subprocess.Popen([str(a.python.absolute()),str(root/'Tools~/reference_server.py')],stdout=subprocess.PIPE,stderr=errors,text=True,env=env)
    try:
        port=peer.stdout.readline().strip()
        if not port.isdigit(): raise SystemExit(f'Peer failed; see {target/"peer.log"}')
        (target/'fixture-port.txt').write_text(port)
        run('VerifyMuseObject.Run','test.log')
        report=(target/'report.txt').read_text()
        print(report)
        if 'FAIL ' in report or 'ERROR ' in report: raise SystemExit(1)
    finally:
        peer.terminate()
        try: peer.wait(timeout=5)
        except subprocess.TimeoutExpired: peer.kill();peer.wait()
