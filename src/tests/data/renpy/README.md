# Ren'Py archive fixtures

## Oversized index fixture

`expanded-index-over-budget.rpa` is a 65,271-byte RPA-2.0 archive. Its index begins
at offset 32 and is a zlib stream that expands to exactly 67,108,865 `N` bytes:
one byte past the 64 MiB metadata budget. The expanded bytes are deliberately not
a valid Pickle; `PrepareFiles` must reject the stream at its size limit first.

SHA-256 of the complete archive:
`47638c44d5e160baaff37211fac2661279277295553ad9784e955738761b8200`.
The independent stdlib verifier is `build/tests/test_renpy_metadata_fixture.py`.

Regenerate from this directory with CPython 3.14.7 and its zlib-ng 1.3.1
compatibility API (the versions used for the recorded hash):

```python
from pathlib import Path
import zlib

expanded_size = 64 * 1024 * 1024 + 1
header = b"RPA-2.0 " + format(32, "016x").encode("ascii") + b"\n"
archive = header.ljust(32, b"\0") + zlib.compress(b"N" * expanded_size, 9)
Path("expanded-index-over-budget.rpa").write_bytes(archive)
```

## RPA-1.0 split-file pair

`rpa1-split.rpa` and `rpa1-split.rpi` are authored test data, unrelated to any
game assets. The 29-byte data file contains padding and three fragments of
`dir/hello.txt`; the index lists the fragments in logical order, deliberately
different from their physical order. Extracting that entry must produce exactly
the 18 ASCII bytes `first:second:third`. The second entry, `empty.txt`, has a
zero-length range at the end of the data file and must extract as an empty file.
The `.rpa` file is the archive entry point; its neighboring `.rpi` file is a
required index sidecar and must not open as an archive by itself.

The 85-byte index is a zlib-compressed protocol-2 Pickle dictionary. Regenerate
both files from this directory with Python's standard library:

```python
from pathlib import Path
import pickle
import zlib

data = b"PAD!third\xfffirst:\x00\x01second:TAIL"
index = {
    "dir/hello.txt": [(10, 6), (18, 7), (4, 5)],
    "empty.txt": [(len(data), 0)],
}
Path("rpa1-split.rpa").write_bytes(data)
Path("rpa1-split.rpi").write_bytes(zlib.compress(pickle.dumps(index, protocol=2), 9))
```

SHA-256 values of the checked-in files:

- `rpa1-split.rpa`: `cc75c66d110b50aa52eed163d72bae23953e2ac0a81a0856a5a0f06a2931ea68`.
- `rpa1-split.rpi`: `03fd8fb47ed04b503141d4520a564c269eb1ff1fcedf1b88980ff6b94c172bef`.
