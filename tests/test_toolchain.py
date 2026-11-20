"""Safety checks for the local-only installer, without network access."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("setup_toolchain", ROOT / "tools/setup_toolchain.py")
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class InstallerSafetyTests(unittest.TestCase):
    def setUp(self):
        parent = ROOT / "local/test-tools"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=parent)
        self.root = Path(self.temp.name)
        self.previous = setup.DOWNLOADS
        setup.DOWNLOADS = self.root / "downloads"
        setup.DOWNLOADS.mkdir()

    def tearDown(self):
        setup.DOWNLOADS = self.previous
        self.temp.cleanup()

    def archive(self, name):
        path = self.root / "test.zip"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(name, "new")
        return path

    def test_rejects_parent_traversal(self):
        with self.assertRaises(RuntimeError):
            setup.extract_zip(self.archive("../escape.txt"), self.root / "out")
        self.assertFalse((self.root / "escape.txt").exists())

    def test_refuses_overwrite(self):
        target = self.root / "out"
        target.mkdir()
        (target / "keep.txt").write_text("original")
        with self.assertRaises(RuntimeError):
            setup.extract_zip(self.archive("keep.txt"), target)
        self.assertEqual((target / "keep.txt").read_text(), "original")

    def test_extracts_safe_paths(self):
        target = self.root / "out"
        setup.extract_zip(self.archive("nested/file.txt"), target)
        self.assertEqual((target / "nested/file.txt").read_text(), "new")

    def test_rejects_publisher_hash_mismatch(self):
        (setup.DOWNLOADS / "cached.bin").write_bytes(b"payload")
        with self.assertRaisesRegex(RuntimeError, "SHA-512 mismatch"):
            setup.download("https://example.invalid/not-requested", "cached.bin", "0" * 128)

    def test_rejects_changed_pinned_download(self):
        path = setup.DOWNLOADS / "cached.bin"
        path.write_bytes(b"original")
        setup.download("https://example.invalid/not-requested", path.name)
        record = json.loads((setup.DOWNLOADS / "manifest.json").read_text())[path.name]
        self.assertEqual(record["sha256"], hashlib.sha256(b"original").hexdigest())
        path.write_bytes(b"modified")
        with self.assertRaisesRegex(RuntimeError, "Pinned download changed"):
            setup.download("https://example.invalid/not-requested", path.name)

    def test_rejects_sha256_pin_on_first_use(self):
        (setup.DOWNLOADS / "cached.bin").write_bytes(b"wrong bytes")
        with self.assertRaisesRegex(RuntimeError, "Pinned SHA-256 mismatch"):
            setup.download("https://example.invalid/not-requested", "cached.bin",
                           expected_sha256="0" * 64)


if __name__ == "__main__":
    unittest.main()
