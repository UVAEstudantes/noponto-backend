import struct
import unittest
from tools.eta_ml.distance_sample import point, direct_meters


class DistanceTests(unittest.TestCase):
    def test_ewkb_point_and_spherical_distance(self):
        hex_wkb=struct.pack('<BII dd',1,0x20000001,4326,-43.0,-22.0).hex()
        self.assertEqual(point(hex_wkb),(-43.0,-22.0))
        self.assertEqual(direct_meters(-22,-43,-22,-43),0)
        self.assertAlmostEqual(direct_meters(0,0,0,1),111195.08,places=1)

    def test_nonpoint_rejected(self):
        with self.assertRaises(ValueError): point(struct.pack('<BI',1,2).hex())
