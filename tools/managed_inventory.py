"""Read ECMA-335 type/field/method metadata without loading or executing an assembly."""
import argparse
import json
from pathlib import Path
import struct


def inventory(path):
    data = Path(path).read_bytes()

    def u16(offset):
        return struct.unpack_from("<H", data, offset)[0]

    def u32(offset):
        return struct.unpack_from("<I", data, offset)[0]

    pe = u32(0x3c)
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Not a PE assembly")
    sections = pe + 24 + u16(pe + 20)
    section_rows = [
        struct.unpack_from("<IIII", data, sections + 40 * i + 8)
        for i in range(u16(pe + 6))
    ]

    def rva(address):
        for virtual_size, virtual_address, raw_size, raw in section_rows:
            if virtual_address <= address < virtual_address + max(virtual_size, raw_size):
                return raw + address - virtual_address
        raise ValueError("Unmapped RVA")

    optional = pe + 24
    directories = optional + (112 if u16(optional) == 0x20b else 96)
    cli = rva(u32(directories + 14 * 8))
    metadata = rva(u32(cli + 8))
    if data[metadata:metadata + 4] != b"BSJB":
        raise ValueError("Missing CLI metadata")
    cursor = metadata + 16 + u32(metadata + 12)
    cursor = (cursor + 3) & ~3
    stream_count = u16(cursor + 2)
    cursor += 4
    streams = {}
    for _ in range(stream_count):
        offset, size = struct.unpack_from("<II", data, cursor)
        end = data.index(b"\0", cursor + 8)
        name = data[cursor + 8:end].decode("ascii")
        streams[name] = (metadata + offset, size)
        cursor = (end + 4) & ~3
    strings = streams["#Strings"][0]
    tables = streams.get("#~", streams.get("#-"))[0]
    heap_flags = data[tables + 6]
    valid = struct.unpack_from("<Q", data, tables + 8)[0]
    cursor = tables + 24
    rows = {}
    for table in range(64):
        if valid & (1 << table):
            rows[table] = u32(cursor)
            cursor += 4
    string_width = 4 if heap_flags & 1 else 2
    guid_width = 4 if heap_flags & 2 else 2
    blob_width = 4 if heap_flags & 4 else 2

    def index(table):
        return 4 if rows.get(table, 0) >= 65536 else 2

    def coded(bits, *targets):
        return 4 if max(rows.get(t, 0) for t in targets) >= 1 << (16 - bits) else 2

    schemas = {
        0: [2, string_width, guid_width, guid_width, guid_width],
        1: [coded(2, 0, 26, 35, 1), string_width, string_width],
        2: [4, string_width, string_width, coded(2, 2, 1, 27), index(4), index(6)],
        3: [index(4)],
        4: [2, string_width, blob_width],
        5: [index(6)],
        6: [4, 2, 2, string_width, blob_width, index(8)],
    }
    decoded = {}
    for table in range(7):
        entries = []
        for _ in range(rows.get(table, 0)):
            entry = []
            for width in schemas[table]:
                entry.append(int.from_bytes(data[cursor:cursor + width], "little"))
                cursor += width
            entries.append(entry)
        decoded[table] = entries
    if rows.get(3) or rows.get(5):
        raise ValueError("Pointer tables are not supported")

    def text(offset):
        start = strings + offset
        return data[start:data.index(b"\0", start)].decode("utf-8")

    types = []
    for number, row in enumerate(decoded[2]):
        next_row = decoded[2][number + 1] if number + 1 < len(decoded[2]) else None
        field_end = next_row[4] if next_row else len(decoded[4]) + 1
        method_end = next_row[5] if next_row else len(decoded[6]) + 1
        types.append({
            "name": ".".join(filter(None, [text(row[2]), text(row[1])])),
            "token": f"0x{0x02000001 + number:08x}",
            "fields": [text(f[1]) for f in decoded[4][row[4] - 1:field_end - 1]],
            "methods": [
                {"name": text(m[3]), "rva": m[0], "token": f"0x{0x06000001 + i:08x}"}
                for i, m in enumerate(decoded[6][row[5] - 1:method_end - 1], row[5] - 1)
            ],
        })
    return types


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assembly", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = inventory(args.assembly)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(f"Read {len(result)} type definitions; saved {args.output}")


if __name__ == "__main__":
    main()
