"""Settings and research-session output for pika op."""
import csv
import json
import math
from pathlib import Path


def read_settings(path, defaults):
    result = dict(defaults)
    if not path.exists():
        return result
    data = json.loads(path.read_text(encoding='utf-8'))
    if not isinstance(data,dict):
        raise ValueError('Settings must be a JSON object')
    for key,default in defaults.items():
        value = data.get(key,default)
        if type(value) is type(default) and (not isinstance(value,float) or math.isfinite(value)):
            result[key] = value
    return result


def write_settings(path, values):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(values,indent=2,allow_nan=False),encoding='utf-8')
    temporary.replace(path)


def filtered_players(players, origin, query='', maximum_distance=0, sort_by='name', descending=False):
    query = query.casefold().strip()
    rows = [p for p in players if (not query or query in
        (p.get('name','')+' '+p.get('weapon',{}).get('name','')).casefold()) and
        (not maximum_distance or math.dist(origin,p['position'])<=maximum_distance)]
    def key(player):
        if sort_by=='distance':
            return math.dist(origin,player['position'])
        if sort_by in ('health','armor'):
            return player.get(sort_by) or 0
        if sort_by=='weapon':
            return player.get('weapon',{}).get('name','').casefold()
        if sort_by=='ammo':
            return player.get('weapon',{}).get('ammo') or 0
        if sort_by=='scoped':
            return bool(player.get('scoped'))
        return player.get('name','').casefold()
    return sorted(rows,key=key,reverse=descending)


CSV_FIELDS = ('timestamp_utc','build','name','health','armor','weapon','ammo','x','y','z','scoped')


def csv_rows(frame):
    for player in frame['players']:
        x,y,z = player['position']
        yield dict(timestamp_utc=frame['timestamp_utc'],build=frame.get('build'),name=player['name'],
                   health=player['health'],armor=player.get('armor'),weapon=player['weapon']['name'],
                   ammo=player['weapon'].get('ammo'),x=x,y=y,z=z,scoped=player.get('scoped'))


def export_csv(path, frame):
    # Exclusive creation prevents accidental replacement of an earlier experiment.
    with path.open('x',encoding='utf-8-sig',newline='') as stream:
        writer = csv.DictWriter(stream,fieldnames=CSV_FIELDS)
        writer.writeheader()
        for row in csv_rows(frame):
            # Spreadsheet programs can interpret untrusted player names as formulas.
            row = {k:("'"+v if isinstance(v,str) and v[:1] in ('=','+','-','@','\t','\r') else v)
                   for k,v in row.items()}
            writer.writerow(row)


class Recorder:
    def __init__(self,path):
        self.path = Path(path)
        self.stream = self.path.open('x',encoding='utf-8')
        self.frames = 0

    def append(self,frame):
        self.stream.write(json.dumps(frame,allow_nan=False)+'\n')
        self.stream.flush()
        self.frames += 1

    def close(self):
        self.stream.close()
