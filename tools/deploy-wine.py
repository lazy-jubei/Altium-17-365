#!/usr/bin/env python3
"""Deploy a locally built extension into an Altium Wine prefix, with backups."""
import argparse
import datetime
import json
import os
from pathlib import Path
import shutil
import tempfile
import uuid
import xml.etree.ElementTree as ET

HRID = 'Altium17-365'
REQUIRED = ('Altium17-365.dll', 'Altium17-365.Core.dll', 'Altium.Auth.dll', 'Newtonsoft.Json.dll', 'Altium17-365.Ins', 'Altium17-365.rcs', 'Altium17-365.target.json', 'THIRD-PARTY-NOTICES.txt')

def validate_target(dist, altium_version):
    if altium_version != '17':
        raise ValueError('This plugin requires Altium Designer 17.')
    manifest = dist / 'Altium17-365.target.json'
    if not manifest.is_file():
        raise ValueError('Missing target manifest. Rebuild for AD17.')
    target = json.loads(manifest.read_text(encoding='utf-8-sig'))
    framework, architecture = ('net48', 'x86')
    if (target.get('altiumVersion'), target.get('framework'), target.get('architecture')) != (altium_version, framework, architecture):
        raise ValueError(f'Package target does not match AD{altium_version} ({framework}/{architecture}).')

def windows_path(path, prefix):
    path, drive = Path(path).resolve(), (Path(prefix) / 'drive_c').resolve()
    try:
        return 'C:\\' + str(path.relative_to(drive)).replace('/', '\\')
    except ValueError:
        return 'Z:' + str(path).replace('/', '\\')

def find_extensions(prefix, explicit=None):
    if explicit:
        root = Path(explicit).resolve()
    else:
        candidates = list((Path(prefix) / 'drive_c/ProgramData/Altium').glob('Altium Designer {*/Extensions/ExtensionsRegistry.xml'))
        if len(candidates) != 1:
            raise ValueError('Specify --extensions-root when zero or multiple Altium installations are found.')
        root = candidates[0].parent
    if not (root / 'ExtensionsRegistry.xml').is_file():
        raise ValueError('No ExtensionsRegistry.xml in the selected extension folder.')
    return root

def deploy(dist, prefix, extensions_root=None, dry_run=False, altium_version='17'):
    dist = Path(dist).resolve()
    validate_target(dist, altium_version)
    required = REQUIRED
    for name in required:
        if not (dist / name).is_file():
            raise ValueError(f'Missing built artifact: {name}')
    files = [p for p in dist.iterdir() if p.is_file()]
    if any(p.name.startswith(('Altium.', 'DevExpress.')) and p.name != 'Altium.Auth.dll' for p in files):
        raise ValueError('The dist folder must not contain Altium SDK or DevExpress assemblies.')
    root = find_extensions(prefix, extensions_root)
    registry = root / 'ExtensionsRegistry.xml'
    tree = ET.parse(registry)
    if tree.getroot().tag != 'Extensions':
        raise ValueError('Invalid extension registry root.')
    existing = tree.getroot().findall(f"Item[@HRID='{HRID}']")
    if len(existing) > 1:
        raise ValueError('Duplicate workspace connector entries in the registry; resolve them before deployment.')
    target = root / HRID
    if dry_run:
        return target, None
    stamp = uuid.uuid4().hex
    backup = root / f'{HRID}.{stamp}.backup'
    backup.mkdir()
    shutil.copy2(registry, backup / registry.name)
    if target.exists():
        shutil.copytree(target, backup / HRID)
    stage = Path(tempfile.mkdtemp(prefix=f'.{HRID}-', dir=root))
    registry_temp = root / f'.ExtensionsRegistry.{stamp}.tmp'
    try:
        for file in files:
            shutil.copy2(file, stage / file.name)
        if existing:
            item = existing[0]
        else:
            item = ET.SubElement(tree.getroot(), 'Item', HRID=HRID, Guid='648B8C3D-7EF0-4AB5-A49C-273FB1AB00E7')
            today = (datetime.datetime.now() - datetime.datetime(1899, 12, 30)).total_seconds() / 86400
            fields = {'Status': '0', 'VaultGuid': '', 'CreatedBy': 'Altium17Cloud contributors',
                      'CategoryGuid': '793A1F67-0B22-4E01-A5DE-3176A1E8C60D', 'CategoryName': '',
                      'ReadMe': '', 'Help': '', 'Requirements': '', 'Title': HRID,
                      'ShortDescription': HRID, 'LongDescription': 'Connect to Altium 365 workspaces',
                      'SmallImage': '', 'LargeImage': '', 'VersionGuid': 'B840AF64-4409-422C-9D96-E1674963B594',
                      'ReleasedDate': f'{today:.7f}', 'ReleaseNotes': '', 'DateInstalled': f'{today:.7f}'}
            for name, value in fields.items():
                ET.SubElement(item, name).text = value
        pv = item.find('PlatformVersions')
        if pv is None:
            pv = ET.SubElement(item, 'PlatformVersions')
        minimums = ('1.0.5.5', '10.0.5.5')
        for name, version in zip(('DXP', 'EDP', 'MaxDXP', 'MaxEDP'), (*minimums, '0.0.0.0', '0.0.0.0')):
            platform = pv.find(name)
            if platform is None:
                platform = ET.SubElement(pv, name)
            platform.set('BuildNumber', version)
        for name, value in [('Path', windows_path(target, prefix)), ('Version', '0.2.0.0')]:
            child = item.find(name)
            if child is None:
                child = ET.SubElement(item, name)
            child.text = value
        ET.indent(tree, space='  ')
        tree.write(registry_temp, encoding='utf-8', xml_declaration=True)
        if target.exists():
            shutil.rmtree(target)
        stage.rename(target)
        os.replace(registry_temp, registry)
    except Exception:
        if target.exists():
            shutil.rmtree(target)
        if (backup / HRID).exists():
            shutil.copytree(backup / HRID, target)
        shutil.copy2(backup / registry.name, registry)
        raise
    finally:
        if stage.exists():
            shutil.rmtree(stage)
        registry_temp.unlink(missing_ok=True)
    return target, backup

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--altium-version', choices=('17',), default='17')
    parser.add_argument('--prefix', type=Path)
    parser.add_argument('--dist', type=Path)
    parser.add_argument('--extensions-root', type=Path)
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    prefix = args.prefix or Path.home() / 'AltiumWine17/prefix'
    dist = args.dist or Path(__file__).resolve().parents[1] / 'dist'
    target, backup = deploy(dist, prefix, args.extensions_root, args.dry_run, args.altium_version)
    print(('Would install into: ' if args.dry_run else 'Installed into: ') + str(target))
    if backup:
        print('Backup: ' + str(backup))
        print('Restart Altium to load the extension.')

if __name__ == '__main__':
    main()
