#!/usr/bin/env python3
"""Контракт сборки: закрепленный исходник, настоящие флаги minOS и полный нужный набор возможностей."""

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
COMMIT = "7054cac5124465ffdb145870d7c01885566711b1"
TAGS = "with_gvisor,with_quic,with_wireguard,with_utls"


class SingboxBuildTest(unittest.TestCase):
    def run_build(self, architecture="arm64", bad_commit=False, bad_modules=False, explicit=True):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "scripts").mkdir()
            (root / "shared/cores").mkdir(parents=True)
            shutil.copyfile(ROOT / "scripts/get-singbox.sh", root / "scripts/get-singbox.sh")
            (root / "shared/cores/sing-box.version").write_text("v1.14.3\n")
            (root / "shared/cores/sing-box.commit").write_text(COMMIT + "\n")
            commands = root / "bin"
            commands.mkdir()
            stub = """#!/usr/bin/env python3
import json, os, pathlib, sys
name = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
if name == 'uname':
    print(os.environ['TEST_ARCH'] if args == ['-m'] else 'Darwin')
elif name == 'git':
    if 'clone' in args:
        assert '--branch' in args and args[args.index('--branch')+1] == 'v1.14.3'
        source = pathlib.Path(args[-1]); (source/'release').mkdir(parents=True)
        (source/'LICENSE').write_text('GPL-3.0 test fixture')
        (source/'release/DEFAULT_BUILD_TAGS_OTHERS').write_text(os.environ['TEST_TAGS'])
        (source/'release/LDFLAGS').write_text('-checklinkname=0')
    elif 'rev-parse' in args:
        print('0'*40 if os.environ['TEST_BAD_COMMIT'] == '1' else os.environ['TEST_COMMIT'])
    elif 'diff' in args:
        sys.exit(1 if os.environ['TEST_BAD_MODULES'] == '1' else 0)
    else: raise SystemExit('unexpected git call')
elif name == 'go':
    if args == ['version']: print('go version go1.27.3 darwin/' + os.environ['TEST_ARCH'])
    else:
        keys = ['GOOS','GOARCH','CGO_ENABLED','MACOSX_DEPLOYMENT_TARGET','CGO_CFLAGS','CGO_CXXFLAGS','CGO_LDFLAGS','GOSUMDB']
        with open(os.environ['TEST_LOG'], 'a') as log:
            log.write(json.dumps({'args':args, 'env':{key:os.environ.get(key) for key in keys}})+'\\n')
        if args[0] == 'build':
            pathlib.Path(args[args.index('-o')+1]).write_text('compiled fixture')
        elif args != ['mod','verify']: raise SystemExit('unexpected go call')
"""
            for command in ("uname", "git", "go", "clang"):
                path = commands / command
                path.write_text(stub)
                path.chmod(0o755)
            log = root / "calls.jsonl"
            environment = dict(os.environ, PATH=str(commands) + os.pathsep + os.environ["PATH"],
                               TEST_ARCH=architecture, TEST_COMMIT=COMMIT, TEST_TAGS=TAGS, TEST_LOG=str(log),
                               TEST_BAD_COMMIT="1" if bad_commit else "0", TEST_BAD_MODULES="1" if bad_modules else "0")
            result = subprocess.run(["bash", str(root / "scripts/get-singbox.sh")] + (["--build"] if explicit else []),
                                    env=environment, capture_output=True, text=True)
            calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
            output = root / "artifacts/cores/sing-box"
            info = (output / "BUILD-INFO.txt").read_text() if output.exists() else None
            return result, calls, info

    def test_both_native_architectures_keep_features_and_a_real_deployment_target(self):
        for architecture, goarch in (("arm64", "arm64"), ("x86_64", "amd64")):
            with self.subTest(architecture=architecture):
                result, calls, info = self.run_build(architecture, explicit=architecture != "arm64")
                self.assertEqual(0, result.returncode, result.stderr)
                build = calls[0]
                self.assertIn("-mod=readonly", build["args"])
                self.assertEqual(TAGS, build["args"][build["args"].index("-tags") + 1])
                self.assertEqual("darwin", build["env"]["GOOS"])
                self.assertEqual(goarch, build["env"]["GOARCH"])
                self.assertEqual("1", build["env"]["CGO_ENABLED"])
                self.assertEqual("13.0", build["env"]["MACOSX_DEPLOYMENT_TARGET"])
                for flag in ("CGO_CFLAGS", "CGO_CXXFLAGS", "CGO_LDFLAGS"):
                    self.assertEqual("-mmacosx-version-min=13.0", build["env"][flag])
                self.assertEqual("sum.golang.org", build["env"]["GOSUMDB"])
                self.assertEqual(["mod", "verify"], calls[1]["args"])
                self.assertIn(COMMIT, info)
                self.assertIn("CGO_ENABLED=1; MACOSX_DEPLOYMENT_TARGET=13.0", info)
                self.assertIn(TAGS, info)

    def test_retargeted_tag_is_rejected_before_any_build(self):
        result, calls, info = self.run_build(bad_commit=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("не совпал", result.stderr)
        self.assertEqual([], calls)
        self.assertIsNone(info)

    def test_changed_dependency_files_are_rejected_before_installing_the_core(self):
        result, _, info = self.run_build(bad_modules=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIsNone(info)


if __name__ == "__main__":
    unittest.main()
