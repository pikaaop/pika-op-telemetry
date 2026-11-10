import csv
import json
from pathlib import Path
import tempfile
import unittest
from viewer_tools import read_settings,write_settings,filtered_players,Recorder,export_csv


class ViewerTests(unittest.TestCase):
    def test_settings_types_and_round_trip(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'settings.json'
            defaults = {'enabled':True,'gain':1.0,'theme':'Emerald'}
            self.assertEqual(read_settings(path,defaults),defaults)
            write_settings(path,{'enabled':False,'gain':.5,'theme':'Blue'})
            self.assertEqual(read_settings(path,defaults)['gain'],.5)
            path.write_text('{"enabled":"yes","gain":NaN,"theme":"Blue"}')
            loaded = read_settings(path,defaults)
            self.assertIs(loaded['enabled'],True)
            self.assertEqual(loaded['gain'],1.0)
            self.assertEqual(loaded['theme'],'Blue')
            path.write_text('[]')
            with self.assertRaises(ValueError):
                read_settings(path,defaults)

    def test_search_distance_and_numeric_sort(self):
        rows = [dict(name='Bravo',health=9,position=(3,4,0),weapon={'name':'awp'}),
                dict(name='Alpha',health=100,position=(30,40,0),weapon={'name':'deagle'})]
        self.assertEqual(filtered_players(rows,(0,0,0),'AWP')[0]['name'],'Bravo')
        self.assertEqual(len(filtered_players(rows,(0,0,0),maximum_distance=10)),1)
        self.assertEqual(filtered_players(rows,(0,0,0),sort_by='health')[0]['health'],9)
        self.assertEqual(filtered_players(rows,(0,0,0),sort_by='distance',descending=True)[0]['name'],'Alpha')

    def test_recording_and_csv_preserve_prior_output(self):
        frame = dict(timestamp_utc='2026-10-25T12:00:00+00:00',build=14192,players=[
            dict(name='=1+1',health=100,armor=50,position=(1,2,3),scoped=False,
                 weapon=dict(name='deagle',ammo=7))])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'session.jsonl'
            recorder = Recorder(path)
            recorder.append(frame)
            recorder.append(frame)
            self.assertEqual(recorder.frames,2)
            recorder.close()
            frames = [json.loads(line) for line in path.read_text().splitlines()]
            self.assertEqual(len(frames),2)
            with self.assertRaises(FileExistsError):
                Recorder(path)
            target = Path(directory)/'players.csv'
            export_csv(target,frame)
            with target.open(encoding='utf-8-sig',newline='') as stream:
                row = next(csv.DictReader(stream))
            self.assertEqual(row['name'],"'=1+1")
            self.assertEqual(row['ammo'],'7')
            with self.assertRaises(FileExistsError):
                export_csv(target,frame)


if __name__=='__main__':
    unittest.main()
