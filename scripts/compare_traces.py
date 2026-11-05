from pathlib import Path
root=Path(__file__).resolve().parents[1]
assert (root/'artifacts/traces.json').read_bytes()==(root/'demo/traces.json').read_bytes(),'Generated trace bytes differ'
print('PASS scenario trace bytes reproduce exactly')
