import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / 'src/PadMaster.Check/bin/Release/dist'
BRIDGE = 'Plugins/PadMaster/'
MANAGED = 'Local/Plugins/Padmaster AI Assistant/'
FILES = {
    BRIDGE + name for name in ('PadMaster.Bridge.dll', 'PadMaster.Bridge.deps.json',
                              'PadMaster.Bridge.runtimeconfig.json', 'System.Speech.dll',
                              'README.md', 'persona/CLAUDE.md', 'persona/AGENTS.md',
                              'PadMaster.Core.dll', 'PadMaster.Controls.dll', 'SkiaSharp.dll', 'libSkiaSharp.dll')
} | {MANAGED + name for name in ('PadMaster.Settings.dll', 'manifest.json', 'icon.png')}
ASSETS = {p.relative_to(ROOT).as_posix(): p for folder in ('controllers', 'games') for p in (ROOT / folder).rglob('*') if p.is_file()}
FILES |= {BRIDGE + name for name in ASSETS}


def staged_files():
    actual = {p.relative_to(STAGE).as_posix() for p in STAGE.rglob('*') if p.is_file()}
    if actual != FILES:
        raise ValueError(f'dist mismatch: missing={sorted(FILES - actual)}, extra={sorted(actual - FILES)}')
    return actual


def main():
    env = dict(os.environ)
    env.setdefault('DOTNET_CLI_HOME', str(ROOT / 'src/PadMaster.Check/obj/cli-home'))
    env['NuGetAudit'] = 'false'
    env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    subprocess.run(['dotnet', 'build', 'src/PadMaster.sln', '-c', 'Release'], cwd=ROOT, env=env, check=True)
    staged_files()
    for name, source in ASSETS.items():
        if (STAGE / BRIDGE / name).read_bytes() != source.read_bytes():
            raise ValueError('stale asset: ' + name)
    source_manifest = (ROOT / 'src/PadMaster.Settings/manifest.json').read_bytes()
    if (STAGE / MANAGED / 'manifest.json').read_bytes() != source_manifest:
        raise ValueError('dist manifest is stale')
    if (STAGE / BRIDGE / 'README.md').read_bytes() != (ROOT / 'src/PadMaster.Bridge/README.md').read_bytes():
        raise ValueError('dist README is stale')
    version = json.loads(source_manifest)['InstalledVersion']
    if not version or any(c not in '0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-' for c in version):
        raise ValueError('invalid package version')
    target = ROOT / 'dist' / f'Padmaster-AI-Assistant-{version}.zip'
    target.parent.mkdir(exist_ok=True)
    temporary = target.with_suffix('.zip.tmp')
    try:
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED) as package:
            for name in sorted(FILES | {'README.md'}):
                source = STAGE / (BRIDGE + 'README.md' if name == 'README.md' else name)
                info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                package.writestr(info, source.read_bytes())
        from make_package_check import verify
        verify(temporary)
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)
    print(target)
    print('SHA-256 ' + hashlib.sha256(target.read_bytes()).hexdigest())


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('FAIL: ' + str(error), file=sys.stderr)
        sys.exit(1)
