"""Exercise early-boot selection without accessing the machine's DRM settings."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
HOOK = ROOT / 'linux/dracut/91preysense-edid/intel-edid.sh'

class IntelEdidTests(unittest.TestCase):
    def test_only_intel_primary_uses_override(self):
        functions = HOOK.read_text().rsplit('preysense_select_edid', 1)[0]
        for owner in ('intel', 'nvidia', None):
            with self.subTest(owner=owner), tempfile.TemporaryDirectory() as folder:
                for name,vendor in (('intel','0x8086'),('nvidia','0x10de')):
                    dev=Path(folder)/'bus/pci/devices'/name
                    dev.mkdir(parents=True)
                    (dev/'vendor').write_text(vendor+'\n')
                    (dev/'boot_vga').write_text(('1' if owner==name else '0')+'\n')
                result=subprocess.run(['sh','-c',functions+'\npreysense_intel_boot_display "$1"','test',folder])
                self.assertEqual(result.returncode==0,owner=='intel')

    def test_uninstall_does_not_require_connected_panel(self):
        spec=importlib.util.spec_from_file_location('enabler',ROOT/'phn16s_refresh_enabler.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with patch('sys.argv',['enabler','--uninstall']), patch.object(module,'uninstall_override') as uninstall, patch.object(module,'find_edp_connector') as detect:
            module.main()
        uninstall.assert_called_once()
        detect.assert_not_called()

    def test_reject_nvidia_connector_before_installing(self):
        spec=importlib.util.spec_from_file_location('enabler',ROOT/'phn16s_refresh_enabler.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with patch.object(module.os,'geteuid',return_value=0),patch.object(module.os,'makedirs') as mkdir:
            with self.assertRaises(ValueError):module.install_override(b'', ['eDP-2'])
        mkdir.assert_not_called()

if __name__ == '__main__':unittest.main()
