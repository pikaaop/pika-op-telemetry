import json
from pathlib import Path
import tempfile
import unittest
from dump_tools import inspect_dump, resolve_dump_folder
from research_ui import Reader


class DumpTests(unittest.TestCase):
    def test_resolve_repository_and_count_schemas(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root/'repository'/'output'
            output.mkdir(parents=True)
            (output/'offsets.json').write_text('{}')
            (output/'client_dll.json').write_text(json.dumps({'client.dll':{
                'classes':{'A':{'fields':{'x':4,'y':8}},'B':{'fields':{}}},'enums':{}}}))
            (output/'info.json').write_text(json.dumps({'build_number':123}))
            self.assertEqual(resolve_dump_folder(root),output)
            report = inspect_dump(root)
            self.assertEqual(report['class_count'],2)
            self.assertEqual(report['field_count'],2)
            self.assertEqual(report['info']['build_number'],123)
            with self.assertRaises(ValueError):
                resolve_dump_folder(root/'missing')

    def test_optional_read_failures_preserve_available_telemetry(self):
        reader = Reader.__new__(Reader)
        reader.read_errors = (ValueError,)
        reader.field = lambda cls,name: {'m_sSanitizedPlayerName':1,'m_ArmorValue':2,
            'm_bIsScoped':3,'m_iShotsFired':4}[name]
        reader.pointer = lambda address: 0
        reader.string = lambda address,fallback: fallback
        def read(address,format):
            if address==102:
                raise ValueError('Object changed')
            return (0,)
        reader.read = read
        reader.weapon = lambda pawn,root: dict(name='ssg08',ammo=8,item_id=40)
        result = reader.extra_player_data(100,200,300,5)
        self.assertIsNone(result['armor'])
        self.assertEqual(result['weapon']['name'],'ssg08')
        self.assertEqual(result['name'],'Player 5')
        self.assertEqual(result['shots'],0)

    def test_remote_string_controls_are_removed(self):
        reader = Reader.__new__(Reader)
        class Memory:
            def read_bytes(self,address,length):
                return b'A\nB\x00xxxx'
        reader.pm = Memory()
        self.assertEqual(reader.string(100),'AB')
        self.assertEqual(reader.string(0,'Unknown'),'Unknown')


if __name__=='__main__':
    unittest.main()
