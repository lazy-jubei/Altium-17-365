import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('deploy', Path(__file__).resolve().parents[1] / 'tools/deploy-wine.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)

class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.dist = self.root / 'dist'; self.dist.mkdir()
        self.prefix = self.root / 'prefix'; self.prefix.mkdir()
        self.extensions = self.prefix / 'drive_c/ProgramData/Altium/AD17/Extensions'; self.extensions.mkdir(parents=True)
        self.registry = self.extensions / 'ExtensionsRegistry.xml'
        self.registry.write_text('<Extensions><Item HRID="Other"><Path>C:\\Other</Path></Item></Extensions>')
        for name in deploy.REQUIRED: (self.dist / name).write_bytes(b'file')
        (self.dist / 'Altium17-365.target.json').write_text(json.dumps({'altiumVersion':'17','framework':'net48','architecture':'x86'}))
    def tearDown(self): self.temp.cleanup()
    def test_installs_auth_library_and_preserves_other_extension(self):
        target, backup = deploy.deploy(self.dist, self.prefix, self.extensions)
        self.assertTrue((target / 'Altium.Auth.dll').is_file())
        self.assertTrue((backup / self.registry.name).is_file())
        tree=ET.parse(self.registry)
        self.assertEqual(len(tree.getroot().findall('Item')),2)
        self.assertIsNotNone(tree.getroot().find("Item[@HRID='Other']"))
    def test_updates_without_duplicates(self):
        deploy.deploy(self.dist, self.prefix, self.extensions)
        deploy.deploy(self.dist, self.prefix, self.extensions)
        self.assertEqual(len(ET.parse(self.registry).getroot().findall("Item[@HRID='Altium17-365']")),1)
    def test_dry_run_leaves_registry_unchanged(self):
        before=self.registry.read_bytes()
        target,backup=deploy.deploy(self.dist,self.prefix,self.extensions,dry_run=True)
        self.assertEqual(before,self.registry.read_bytes())
        self.assertFalse(target.exists()); self.assertIsNone(backup)
    def test_missing_dependency_rejected(self):
        (self.dist/'Altium.Auth.dll').unlink()
        with self.assertRaisesRegex(ValueError,'Missing built artifact'): deploy.deploy(self.dist,self.prefix,self.extensions)
    def test_vendor_sdk_not_shipped(self):
        (self.dist/'Altium.SDK.dll').write_bytes(b'bad')
        with self.assertRaisesRegex(ValueError,'SDK'): deploy.deploy(self.dist,self.prefix,self.extensions)
    def test_wrong_target_rejected(self):
        (self.dist/'Altium17-365.target.json').write_text(json.dumps({'altiumVersion':'26','framework':'net48','architecture':'x86'}))
        with self.assertRaisesRegex(ValueError,'target'): deploy.deploy(self.dist,self.prefix,self.extensions)

if __name__=='__main__': unittest.main()
