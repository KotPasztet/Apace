import json
import os
import re
import sys

root = sys.argv[1]
errors = []
files = []
for dirpath, _, names in os.walk(root):
    for name in names:
        files.append(os.path.relpath(os.path.join(dirpath, name), root).replace('\\', '/'))

meta = json.load(open(os.path.join(root, 'pack.mcmeta'), encoding='utf-8'))
if meta['pack']['pack_format'] != 26:
    errors.append('pack_format is not 26')


def resource(kind, ref):
    ns, path = ref.split(':', 1)
    ext = {'functions': '.mcfunction'}.get(kind, '.json')
    return f'data/{ns}/{kind}/{path}{ext}'


def check(kind, ref, where):
    if ref.startswith('minecraft:') and kind != 'tags/functions':
        return
    if resource(kind, ref) not in files:
        errors.append(f'{where}: {kind} {ref} does not resolve')


def check_tag(kind, ref, where):
    ns, path = ref.split(':', 1)
    if f'data/{ns}/tags/{kind}/{path}.json' not in files:
        errors.append(f'{where}: tag #{ref} ({kind}) does not resolve')


objectives_created = set()
objectives_used = {}
events = set()

for rel in files:
    text = open(os.path.join(root, rel), encoding='utf-8').read()
    if rel.endswith('.mcfunction'):
        for i, line in enumerate(text.splitlines(), 1):
            where = f'{rel}:{i}'
            if line.strip().startswith('#'):
                errors.append(f'{where}: comment line')
            for ref in re.findall(r'\bfunction ([a-z0-9_.-]+:[a-z0-9_/.-]+)', line):
                check('functions', ref, where)
            for ref in re.findall(r'predicate[ =]([a-z0-9_.-]+:[a-z0-9_/.-]+)', line):
                check('predicates', ref, where)
            for ref in re.findall(r'type=#([a-z0-9_.-]+:[a-z0-9_/.-]+)', line):
                check_tag('entity_types', ref, where)
            for ref in re.findall(r'unless block [^#]*#([a-z0-9_.-]+:[a-z0-9_/.-]+)', line):
                check_tag('blocks', ref, where)
            for ref in re.findall(r'advancement (?:grant|revoke) \S+ (?:only|from|until|through) ([a-z0-9_.-]+:[a-z0-9_/.-]+)', line):
                check('advancements', ref, where)
            for ref in re.findall(r'\bloot\b.*\bloot ([a-z0-9_.-]+:[a-z0-9_/.-]+)', line):
                errors.append(f'{where}: loot table {ref} referenced')
            m = re.match(r'scoreboard objectives add (\S+) ', line)
            if m:
                objectives_created.add(m.group(1))
            for obj in re.findall(r'scores=\{(\w+)=', line) + re.findall(r'scoreboard players \w+ @s (\w+)', line) + re.findall(r'if score @s (\w+)', line):
                objectives_used.setdefault(obj, where)
            for ev in re.findall(r'\[apace-event\] (\S+)', line):
                events.add(ev)
    elif rel.endswith('.json') or rel.endswith('.mcmeta'):
        data = json.loads(text)
        if rel.startswith('data/minecraft/tags/functions/'):
            for ref in data['values']:
                check('functions', ref, rel)
        for ref in re.findall(r'"function":\s*"([^"]+)"', text):
            check('functions', ref, rel)
        for ref in re.findall(r'"type":\s*"#([^"]+)"', text):
            check_tag('entity_types', ref, rel)

for obj, where in objectives_used.items():
    if obj not in objectives_created:
        errors.append(f'{where}: objective {obj} is never created in load')

for rel in files:
    if rel.endswith('.mcfunction'):
        fid = rel[len('data/'):].replace('/functions/', ':', 1)[:-len('.mcfunction')]
        referenced = any(fid in open(os.path.join(root, other), encoding='utf-8').read() for other in files if other != rel)
        if not referenced:
            errors.append(f'{rel}: function {fid} is never referenced')

print('files:', len(files))
print('events:', ' '.join(sorted(events)))
if errors:
    print('\n'.join(errors))
    sys.exit(1)
print('ok')
