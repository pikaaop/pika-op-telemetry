"""Inspect schema exports without executing anything from a dump folder."""
import json
from pathlib import Path


def resolve_dump_folder(folder):
    folder = Path(folder)
    candidates = [folder, folder/'output', *sorted(folder.glob('*/output'))]
    for candidate in candidates:
        if (candidate/'offsets.json').is_file() and (candidate/'client_dll.json').is_file():
            return candidate
    raise ValueError('Select a dump folder containing offsets.json and client_dll.json')


def inspect_dump(folder):
    folder = resolve_dump_folder(folder)
    result = dict(folder=str(folder),info={},modules={},file_count=0)
    for path in sorted(folder.glob('*.json')):
        data = json.loads(path.read_text(encoding='utf-8'))
        result['file_count'] += 1
        if path.name == 'info.json':
            result['info'] = data
        if not isinstance(data,dict):
            continue
        for module,content in data.items():
            if not isinstance(content,dict) or 'classes' not in content:
                continue
            classes = content['classes']
            result['modules'][module] = dict(file=path.name,classes=len(classes),
                fields=sum(len(c.get('fields',{})) for c in classes.values()),
                enums=len(content.get('enums',{})))
    result['class_count'] = sum(m['classes'] for m in result['modules'].values())
    result['field_count'] = sum(m['fields'] for m in result['modules'].values())
    return result
