# AC-1353 spike: static sweep of UI coupling in Cockpit.App and the in-repo plugins.
# Run from the repo root: python spikes/spike-backend-host/sweep.py [app|plugins]
import collections, json, os, re, sys

UI = dict(
    avalonia=r'^using Avalonia|Avalonia\.',
    uithread=r'Dispatcher\.UIThread',
    uicall=r'UiThreadCall\.',
    dtimer=r'\bDispatcherTimer\b',
    cvm=r'\bCockpitViewModel\b',
)


def scan(root):
    rows = []
    for d, _, fs in os.walk(root):
        norm = d.replace(os.sep, '/')
        if '/obj' in norm or '/bin' in norm:
            continue
        for f in fs:
            if not f.endswith('.cs'):
                continue
            p = os.path.join(d, f).replace(os.sep, '/')
            s = open(p, encoding='utf-8', errors='ignore').read()
            r = dict(path=p, lines=s.count('\n'))
            for k, rx in UI.items():
                r[k] = len(re.findall(rx, s, re.M))
            r['singleton'] = bool(re.search(r'\bISingletonService\b', s))
            r['hosted'] = bool(re.search(r'\bIHostedService\b|\bBackgroundService\b', s))
            r['coupled'] = any(r[k] for k in UI)
            rows.append(r)
    return rows


def app():
    rows = scan('src/Cockpit.App')
    print('files', len(rows), 'lines', sum(r['lines'] for r in rows))
    for k in UI:
        print(f'{k:9} files={sum(1 for r in rows if r[k]):4} hits={sum(r[k] for r in rows)}')
    svc = [r for r in rows if r['singleton'] or r['hosted']]
    clean = [r for r in svc if not r['coupled']]
    print(f'\nDI services in App: {len(svc)} files, {sum(r["lines"] for r in svc)} lines;'
          f' UI-free {len(clean)} ({sum(r["lines"] for r in clean)} lines); coupled {len(svc) - len(clean)}')
    by = collections.defaultdict(list)
    for r in svc:
        by[r['path'].split('/')[2] if r['path'].count('/') > 2 else '.'].append(r)
    for k, v in sorted(by.items()):
        print(f'  {k:14} services={len(v):3} coupled={sum(1 for r in v if r["coupled"]):3}')
    json.dump(rows, open('app-sweep.json', 'w'), indent=0)


def plugins():
    base = 'plugins-dev'
    for name in sorted(os.listdir(base)):
        if not name.startswith('Cockpit.Plugin.') or name.endswith('.Tests'):
            continue
        rows = scan(os.path.join(base, name))
        av = [r for r in rows if r['avalonia']]
        timer = sum(r['dtimer'] + r['uithread'] for r in rows)
        csproj = open(os.path.join(base, name, name + '.csproj'), encoding='utf-8').read()
        axaml = sum(1 for d, _, fs in os.walk(os.path.join(base, name)) for f in fs if f.endswith('.axaml'))
        print(f'{name[15:]:22} files={len(rows):3} lines={sum(r["lines"] for r in rows):6}'
              f' avaloniaFiles={len(av):3} axaml={axaml:3} dispatcher={timer:3}'
              f' refsAvaloniaPkg={"Avalonia" in csproj}')


{'app': app, 'plugins': plugins}[sys.argv[1] if len(sys.argv) > 1 else 'app']()
