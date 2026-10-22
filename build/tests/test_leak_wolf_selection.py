"""Tests-first maintained leak selection: no native tools or registry operations."""
import sys
from pathlib import Path
import tempfile
import unittest
from unittest import mock
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main as driver
import leaks as worker
WOLF=('wolf-small-success','wolf-malformed','wolf-cancellation')
class WolfLeakSelection(unittest.TestCase):
    def test_real_worker_accepts_each_wolf_scenario_in_both_modes(self):
        for mode in ('operations','lifecycle'):
            for scenario in WOLF:
                with self.subTest(mode=mode,scenario=scenario):
                    worker._selection(mode,scenario)
    def test_real_driver_schedules_each_wolf_scenario_and_retains_windows(self):
        with tempfile.TemporaryDirectory() as t:
            repository=Path(t); calls=[]
            sources=tuple(repository/n for n in worker.BINARIES)
            with mock.patch.object(driver,'_leak_sources',return_value=sources),mock.patch.object(driver,'_find_umdh',return_value=repository/'umdh.exe'),mock.patch.object(driver,'run',side_effect=lambda argv,**kw:calls.append((argv,kw))):
                driver._leak_measure(repository)
            self.assertEqual(tuple(map(str,sources)),tuple(calls[0][0][4:]))
            measures=[argv[4:] for argv,_ in calls if argv[3]=='measure']
            for mode in ('operations','lifecycle'):
                for scenario in WOLF:
                    matches=[m for m in measures if m[2:4]==[mode,scenario]]
                    self.assertEqual(len(matches),1,(mode,scenario,measures))
                    self.assertEqual(matches[0][4:],['8','100','3','0'])
    def test_worker_measure_routes_wolf_to_real_capture_contract(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t)
            with mock.patch.dict(worker.os.environ,{'UNPACKRAT_OUT_DIR':t}),mock.patch.object(worker,'_capture',return_value=root) as capture,mock.patch.object(worker,'_diff',return_value={}),mock.patch.object(worker,'_summary',return_value=True):
                worker._measure(['prepared','umdh.exe','lifecycle','wolf-cancellation','8','100','3','0'])
            capture.assert_called_once_with('prepared','umdh.exe','lifecycle','wolf-cancellation',8,100,3)
if __name__=='__main__':unittest.main()
