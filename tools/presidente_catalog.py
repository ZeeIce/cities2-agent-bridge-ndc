import pathlib, re, json
root = pathlib.Path(__file__).resolve().parent
source = root.parent / 'presidente-re/decompiled/HomelandSecurity.UI'
actions, reads = [], []
for file in sorted(source.glob('*.cs')):
    text = file.read_text(encoding='utf-8-sig')
    for m in re.finditer(r'new TriggerBinding(?:<([^>]+)>)?\("([^"]+)", "([^"]+)"', text):
        args = (m[1] or '').split(',') if m[1] else []
        # These operate on files, navigation or wholesale reset rather than scoped gameplay decisions.
        blocked = file.stem in {'DataExportUISystem', 'HomelandDashboardUISystem', 'WhatsNewUISystem', 'PerfProbeUISystem'} or re.search(r'^(open|export)|resetAllData', m[3], re.I) is not None
        tail = text[m.end():m.end()+800]
        delegate = re.search(r'delegate\(([^)]*)\)', tail)
        params = [p.strip() for p in delegate[1].split(',')] if delegate else []
        actions.append(dict(id=m[2]+'.'+m[3], system=file.stem, group=m[2], name=m[3], types=[a.strip() for a in args], parameters=params, enabled=not blocked, reason='file_navigation_or_global_reset' if blocked else '', sourceLine=text[:m.start()].count('\n')+1))
    for m in re.finditer(r'new RawValueBinding\("([^"]+)", "([^"]+)"', text):
        reads.append(dict(id=m[1]+'.'+m[2],system=file.stem,group=m[1],name=m[2]))
catalog = dict(actions=actions, reads=reads)
(root/'docs/catalog.json').write_text(json.dumps(catalog,indent=2),encoding='utf-8')
lines = ['// Generated metadata from the locally inspected Presidente build; no implementation copied.', 'namespace PresidenteBridge { internal static class KnownActions { internal static readonly string[] Allowed = new string[] {']
lines += ['    '+json.dumps(a['id'])+',' for a in actions if a['enabled']]
lines += ['}; } }']
(root/'src/KnownActions.cs').write_text('\n'.join(lines),encoding='utf-8')
print(f'{len(actions)} actions, {sum(a["enabled"] for a in actions)} allowed; {len(reads)} raw data bindings')
