#!/usr/bin/env python3
import unittest

from ocr_sidecar import box_to_bounds


class OcrSidecarTests(unittest.TestCase):
    def test_maps_polygon_to_positive_bounds(self) -> None:
        self.assertEqual({"x": 10.0, "y": 20.0, "width": 100.0, "height": 30.0}, box_to_bounds([[10, 20], [110, 20], [110, 50], [10, 50]]))

    def test_rejects_invalid_polygon(self) -> None:
        self.assertIsNone(box_to_bounds([[1, 2]]))


if __name__ == "__main__":
    unittest.main()
