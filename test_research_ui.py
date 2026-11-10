import math
import unittest
from research_ui import resolve_entity, validate_layout, choose_target, tracking_step, calibrated_step, projected_bounds, load_skeleton_profile
from pathlib import Path
import tempfile
import json


class TrackingTests(unittest.TestCase):
    def test_chunk_boundary_and_layout_validation(self):
        root, controller, pawn = 0x1000, 0x5000, 0x6000
        memory = {root+0x10:0x10000, root+0x18:0x20000,
                  0x10000+0x70*1:controller, 0x20000+0x70*3:pawn}
        pointer = lambda address: memory.get(address,0)
        self.assertEqual(resolve_entity(pointer,root,515,0x70),pawn)
        self.assertEqual(validate_layout(pointer,lambda a:515,root,controller,pawn,0),0x70)
        with self.assertRaises(ValueError):
            validate_layout(pointer,lambda a:515,root,controller,pawn+8,0)

    def test_target_lock_and_radius(self):
        players = [dict(address=1,aim=(11,0)),dict(address=2,aim=(4,0))]
        self.assertEqual(choose_target(players,(0,0),20)['address'],2)
        self.assertEqual(choose_target(players,(0,0),20,1)['address'],1)
        self.assertEqual(choose_target(players,(0,0),5,1)['address'],2)
        self.assertIsNone(choose_target(players,(0,0),1))

    def test_bounded_motion_and_fractional_accumulation(self):
        counts,_ = tracking_step((10000,-10000),(0,0),.05,.1,1)
        self.assertEqual(counts,(40,-40))
        residual = (0,0)
        total = 0
        for _ in range(100):
            counts,residual = tracking_step((1,0),(0,0),.01,.1,.1,residual,deadzone=0)
            total += counts[0]
        self.assertGreater(total,0)
        counts,_ = tracking_step((10,10),(0,0),0,.1,1)
        self.assertEqual(counts,(0,0))

    def test_deadzone_clears_residual(self):
        self.assertEqual(tracking_step((1,0),(0,0),.01,.1,1,(.4,.4)),((0,0),(0,0)))

    def test_collision_projection(self):
        identity = (1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1)
        self.assertEqual(projected_bounds((0,0,0),(-.5,-.5,0),(.5,.5,1),identity,800,600),
                         (200,150,600,450))

    def test_zoom_and_sensitivity_conversion(self):
        normal = (1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,1,0)
        zoomed = (2,0,0,0, 0,2,0,0, 0,0,1,0, 0,0,1,0)
        a,_ = calibrated_step((1020,1000),(1000,1000),normal,1,1,.05,.5)
        b,_ = calibrated_step((1040,1000),(1000,1000),zoomed,1,1,.05,.5)
        self.assertEqual(a,b)  # Same world angle under different projection scales.
        slower,_ = calibrated_step((1020,1000),(1000,1000),normal,2,1,.05,.5)
        scoped,_ = calibrated_step((1040,1000),(1000,1000),zoomed,1,.5,.05,.5)
        self.assertLess(slower[0],a[0])
        self.assertGreater(scoped[0],a[0])
        with self.assertRaises(ValueError):
            calibrated_step((1020,1000),(1000,1000),normal,0,1,.05,.5)

    def test_boxes_clip_to_viewport(self):
        identity = (1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1)
        self.assertEqual(projected_bounds((0,0,0),(-2,-.5,0),(.5,.5,1),identity,800,600),
                         (0,150,600,450))
        self.assertIsNone(projected_bounds((10,0,0),(-.5,-.5,0),(.5,.5,1),identity,800,600))

    def test_skeleton_requires_explicit_layout(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'profile.json'
            self.assertIsNone(load_skeleton_profile(path))
            path.write_text(json.dumps(dict(pointer_offset=0,stride=32,position_offset=0,
                                            joints={'head':6,'neck':5},edges=[['head','neck']])))
            self.assertEqual(load_skeleton_profile(path)['stride'],32)
            path.write_text('{}')
            with self.assertRaises(ValueError):
                load_skeleton_profile(path)


if __name__ == '__main__':
    unittest.main()
