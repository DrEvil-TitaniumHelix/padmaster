import json
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tempfile
import zipfile

from make_package import ROOT, FILES, BRIDGE, MANAGED


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify(path):
    with tempfile.TemporaryDirectory(prefix='PadMaster-fresh-') as scratch, zipfile.ZipFile(path) as package:
        names = package.namelist()
        require(len(names) == len(set(names)) == 11 and set(names) == FILES | {'README.md'}, 'package must contain exactly the ten dist files and root README.md')
        username = ''.join(map(chr, (102, 105, 115, 99, 104)))
        needles = [f'Users{separator}{username}'.lower().encode(encoding)
                   for separator in ('/', '\\') for encoding in ('utf-8', 'utf-16-le', 'utf-16-be')]
        root = Path(scratch).resolve()
        for entry in package.infolist():
            name = entry.filename
            parts = PurePosixPath(name)
            require(not parts.is_absolute() and '..' not in parts.parts and '\\' not in name and ':' not in name, f'unsafe path: {name}')
            require(name == 'README.md' or name.startswith(BRIDGE) or name.startswith(MANAGED), f'outside package roots: {name}')
            require(not name.lower().endswith('.pdb'), 'PDB in package')
            require((entry.external_attr >> 16) & 0o170000 != 0o120000, 'symlink in package')
            data = package.read(entry)
            require(not any(needle in data.lower() for needle in needles), f'personal path in {name}')
            destination = (root / name).resolve()
            require(destination.is_relative_to(root), 'zip-slip path')
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
        require((root / 'README.md').read_bytes() == (root / BRIDGE / 'README.md').read_bytes(), 'root README differs from plugin README')
        require((root / BRIDGE / 'persona/CLAUDE.md').read_bytes() == (root / BRIDGE / 'persona/AGENTS.md').read_bytes(), 'assistant personas differ')
        speech = list(root.rglob('System.Speech.dll'))
        require(len(speech) == 1 and speech[0].stat().st_size == 686888, 'expected one 686888-byte System.Speech.dll')
        require(not (root / BRIDGE / 'manifest.json').exists() and not list((root / BRIDGE).rglob('PadMaster.Settings.dll')), 'bridge must have no manifest or Settings DLL')
        require(not (root / 'Local/Plugins/.data').exists(), 'fresh install contains a settings store')
        return sorted(names)


def main():
    version = json.loads((ROOT / 'src/PadMaster.Settings/manifest.json').read_bytes())['InstalledVersion']
    path = Path(sys.argv[1]) if len(sys.argv) == 2 else ROOT / 'dist' / f'Padmaster-AI-Assistant-{version}.zip'
    require(len(sys.argv) <= 2, 'usage: make_package_check.py [package.zip]')
    names = verify(path)
    check = ROOT / 'src/PadMaster.Check/bin/Release/net10.0-windows/PadMaster.Check.exe'
    require(check.exists(), f'build first: {check} is missing')
    result = subprocess.run([str(check), '--fresh-default'], cwd=ROOT, capture_output=True, text=True, timeout=120)
    require(result.returncode == 0, 'default settings Check failed: ' + result.stdout + result.stderr)
    for name in names:
        print(name)
    print('OK  fresh-user package: exact eleven files, safe paths, no personal paths/PDB, one Speech DLL, matching README and personas, missing store disabled with activation off')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('FAIL: ' + str(error), file=sys.stderr)
        sys.exit(1)
