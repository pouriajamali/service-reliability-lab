"""Build/check a self-contained replay; no dependency install or network request."""
from pathlib import Path
import json,sys
ROOT=Path(__file__).resolve().parents[1]
raw=(ROOT/'demo/traces.json').read_text(encoding='utf8')
data=json.loads(raw)
assert data['schema']=='service-reliability-replay-v1'
expected={'healthy':(1,'Closed',0),'transient':(2,'Closed',0),'exhausted':(6,'Open',1),'recovery':(7,'Closed',1)}
assert {s['id'] for s in data['scenarios']}==set(expected)
for s in data['scenarios']:
    calls,state,blocked=expected[s['id']]
    assert (s['dependencyCalls'],s['finalCircuit'])==(calls,state),s['id']
    assert sum(e['event']=='attempt' for e in s['events'])==calls
    assert sum(e['event']=='rejected' for e in s['events'])==blocked
    assert all(e['correlationId'].startswith('read-') for e in s['events'])
    assert s['results'][-1]['success']==(s['id']!='exhausted')
html=(ROOT/'demo/template.html').read_text(encoding='utf8').replace('__TRACE_JSON__',raw.replace('</','<\\/'))
target=ROOT/'demo/index.html'
if '--check' in sys.argv:
    assert target.read_text(encoding='utf8')==html,'Rebuild demo/index.html from current traces.'
    print('PASS four trace oracles, exact embedded fixtures and generated HTML')
else:
    target.write_text(html,encoding='utf8',newline='\n')
    print('Built demo/index.html')
