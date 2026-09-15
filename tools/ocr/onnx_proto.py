"""Just enough protobuf to read and rewrite an ONNX model without the onnx package."""
import struct

VARINT, I64, LEN, I32 = 0, 1, 2, 5
DTYPES = {1: "float32", 2: "uint8", 3: "int8", 6: "int32", 7: "int64", 10: "float16", 11: "double"}


def read_varint(buf, pos):
    shift = result = 0
    while True:
        b = buf[pos]
        pos += 1
        result |= (b & 0x7F) << shift
        if not b & 0x80:
            return result, pos
        shift += 7


def fields(buf):
    """Yield (number, wire_type, value) in order; value is int or bytes."""
    pos, end = 0, len(buf)
    while pos < end:
        key, pos = read_varint(buf, pos)
        num, wt = key >> 3, key & 7
        if wt == VARINT:
            val, pos = read_varint(buf, pos)
        elif wt == LEN:
            n, pos = read_varint(buf, pos)
            val = bytes(buf[pos:pos + n])
            pos += n
        elif wt == I64:
            val = bytes(buf[pos:pos + 8]); pos += 8
        elif wt == I32:
            val = bytes(buf[pos:pos + 4]); pos += 4
        else:
            raise ValueError(f"wire type {wt} at {pos}")
        yield num, wt, val


def write_varint(n):
    out = bytearray()
    if n < 0:
        n += 1 << 64
    while True:
        b = n & 0x7F
        n >>= 7
        out.append(b | (0x80 if n else 0))
        if not n:
            return bytes(out)


def field(num, wt, val):
    head = write_varint((num << 3) | wt)
    if wt == VARINT:
        return head + write_varint(val)
    if wt == LEN:
        return head + write_varint(len(val)) + val
    return head + val


def strings(buf, num):
    return [v.decode("utf-8") for n, wt, v in fields(buf) if n == num and wt == LEN]


def ints(buf, num):
    out = []
    for n, wt, v in fields(buf):
        if n != num:
            continue
        if wt == VARINT:
            out.append(v if v < (1 << 63) else v - (1 << 64))
        elif wt == LEN:  # packed
            p = 0
            while p < len(v):
                x, p = read_varint(v, p)
                out.append(x if x < (1 << 63) else x - (1 << 64))
    return out


def node_info(buf):
    attrs = {}
    for n, wt, v in fields(buf):
        if n == 5:
            name = strings(v, 1)[0]
            a_ints, a_i = ints(v, 8), ints(v, 3)
            attrs[name] = a_ints if a_ints else (a_i[0] if a_i else "?")
    return {"inputs": strings(buf, 1), "outputs": strings(buf, 2), "name": (strings(buf, 3) or [""])[0],
            "op": strings(buf, 4)[0], "domain": (strings(buf, 7) or [""])[0], "attrs": attrs}


def tensor_info(buf):
    raw = [v for n, wt, v in fields(buf) if n == 9]
    floats = [v for n, wt, v in fields(buf) if n == 4]
    return {"name": (strings(buf, 8) or [""])[0], "dims": ints(buf, 1), "dtype": (ints(buf, 2) or [0])[0],
            "raw": raw[0] if raw else None, "float_data": floats[0] if floats else None,
            "int32_data": ints(buf, 5), "int64_data": ints(buf, 7)}


def tensor_values(t):
    """Values of a tensor as a flat Python list (float, int8/uint8, int32, int64)."""
    dt, raw = t["dtype"], t["raw"]
    if raw is not None:
        fmt = {1: "f", 2: "B", 3: "b", 6: "i", 7: "q"}[dt]
        return list(struct.unpack("<" + fmt * (len(raw) // struct.calcsize(fmt)), raw))
    if dt == 1 and t["float_data"] is not None:
        v = t["float_data"]
        return list(struct.unpack("<" + "f" * (len(v) // 4), v))
    if dt in (2, 3, 6):
        vals = t["int32_data"]
        return [x - 256 if dt == 3 and x > 127 else x for x in vals]
    if dt == 7:
        return t["int64_data"]
    return []
