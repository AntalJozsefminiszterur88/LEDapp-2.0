import sys
import types
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


def _ensure_dummy_bleak(monkeypatch):
    """Create a dummy bleak module if bleak is missing."""
    if "bleak" not in sys.modules:
        dummy = types.ModuleType("bleak")
        dummy.BleakError = type("BleakError", (Exception,), {})
        dummy.BleakClient = object
        dummy.BleakScanner = object
        monkeypatch.setitem(sys.modules, "bleak", dummy)


import importlib  # noqa: E402


def setup_module(module):
    # ensure bleak dummy present and reload controller
    from _pytest.monkeypatch import MonkeyPatch

    mp = MonkeyPatch()
    _ensure_dummy_bleak(mp)
    module.bc = importlib.import_module("core.ble_controller")


def test_is_bluetooth_off_error_by_winerror():
    controller = bc.BLEController()  # noqa: F821
    err = OSError("Das Gerät kann nicht verwendet werden")
    err.winerror = -2147020577
    assert controller._is_bluetooth_off_error(err)


def test_is_bluetooth_off_error_by_message():
    controller = bc.BLEController()  # noqa: F821
    err = (
        bc.BleakError("Bluetooth adapter is off")  # noqa: F821
        if hasattr(bc, "BleakError")  # noqa: F821
        else Exception("Bluetooth adapter is off")
    )
    assert controller._is_bluetooth_off_error(err)
