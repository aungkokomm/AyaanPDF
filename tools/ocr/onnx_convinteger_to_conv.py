"""Replace dynamically quantized convolutions with plain float Conv.

Each ConvInteger in the model is a chain written by onnxruntime's dynamic
quantizer:
    DynamicQuantizeLinear(x) -> xq, xs, xzp
    Mul(xs, w_scale) -> s
    ConvInteger(xq, wq, xzp, wzp) -> y
    Cast(y) -> yf;  Mul(yf, s) -> ys;  Reshape(b, shape) -> br;  Add(ys, br) -> out
which equals Conv(x, (wq - wzp) * w_scale, b) -> out, up to rounding.
"""
import struct
import sys
import pathlib

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from onnx_proto import LEN, VARINT, field, fields, node_info, strings, tensor_info, tensor_values

src, dst = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
model_fields = list(fields(src.read_bytes()))
graph_fields = list(fields(next(v for n, wt, v in model_fields if n == 7)))

nodes = [[v, node_info(v)] for n, wt, v in graph_fields if n == 1]
inits = {}
init_raw = {}
for n, wt, v in graph_fields:
    if n == 5:
        t = tensor_info(v)
        inits[t["name"]] = t
        init_raw[t["name"]] = v
graph_outputs = {s for n, wt, v in graph_fields if n == 12 for s in strings(v, 1)}

producer, consumers = {}, {}
for i, (_, info) in enumerate(nodes):
    for o in info["outputs"]:
        producer[o] = i
    for x in info["inputs"]:
        consumers.setdefault(x, []).append(i)


def only(name, op):
    users = consumers.get(name, [])
    assert len(users) == 1 and nodes[users[0]][1]["op"] == op, f"{name} feeds {[nodes[u][1]['op'] for u in users]}, expected one {op}"
    return users[0]


removed, replacement, new_inits = set(), {}, []
for c, (raw, info) in enumerate(nodes):
    if info["op"] != "ConvInteger":
        continue
    xq, wq, xzp, wzp = info["inputs"]
    dql = producer[xq]
    assert nodes[dql][1]["op"] == "DynamicQuantizeLinear"
    x = nodes[dql][1]["inputs"][0]
    xs = nodes[dql][1]["outputs"][1]
    cast = only(info["outputs"][0], "Cast")
    mul2 = only(nodes[cast][1]["outputs"][0], "Mul")
    scale = next(s for s in nodes[mul2][1]["inputs"] if s != nodes[cast][1]["outputs"][0])
    mul1 = producer[scale]
    assert nodes[mul1][1]["op"] == "Mul" and xs in nodes[mul1][1]["inputs"]
    w_scale = next(s for s in nodes[mul1][1]["inputs"] if s != xs)
    add = only(nodes[mul2][1]["outputs"][0], "Add")
    bias_reshaped = next(s for s in nodes[add][1]["inputs"] if s != nodes[mul2][1]["outputs"][0])
    reshape = producer[bias_reshaped]
    assert nodes[reshape][1]["op"] == "Reshape"
    bias = nodes[reshape][1]["inputs"][0]
    assert consumers[xq] == [c] and consumers[xzp] == [c] and consumers[xs] == [mul1], "quantized input is shared"

    q = tensor_values(inits[wq])
    zp = tensor_values(inits[wzp])
    s = tensor_values(inits[w_scale])
    assert len(zp) == 1 and len(s) == 1, "per-channel weights not handled"
    w_name = wq.replace("_quantized", "") + "_float"
    w_float = struct.pack(f"<{len(q)}f", *[(v - zp[0]) * s[0] for v in q])
    tensor = b"".join(field(1, VARINT, d) for d in inits[wq]["dims"]) + field(2, VARINT, 1) + field(8, LEN, w_name.encode()) + field(9, LEN, w_float)
    new_inits.append(tensor)

    out = nodes[add][1]["outputs"][0]
    conv = (field(1, LEN, x.encode()) + field(1, LEN, w_name.encode()) + field(1, LEN, bias.encode())
            + field(2, LEN, out.encode()) + field(3, LEN, (info["name"] or out).replace("ConvInteger", "Conv").encode())
            + field(4, LEN, b"Conv")
            + b"".join(field(n, wt, v) for n, wt, v in fields(raw) if n == 5))
    replacement[c] = conv
    removed.update({dql, mul1, cast, mul2, add, reshape})
    print(f"  {info['name'] or out}: {inits[wq]['dims']} scale {s[0]:.3g} zero {zp[0]}")

kept = [replacement.get(i, raw) for i, (raw, info) in enumerate(nodes) if i not in removed]
used = {x for raw in kept for x in node_info(raw)["inputs"]} | graph_outputs
kept_inits = [init_raw[name] for name in init_raw if name in used]
dropped = [name for name in init_raw if name not in used]


def rebuild(original, swaps):
    out, emitted = bytearray(), set()
    for n, wt, v in original:
        if n in swaps:
            if n not in emitted:
                out += b"".join(field(n, LEN, item) for item in swaps[n])
                emitted.add(n)
            continue
        out += field(n, wt, v)
    return bytes(out)


graph = rebuild(graph_fields, {1: kept, 5: kept_inits + new_inits})
model = rebuild(model_fields, {7: [graph]})
dst.write_bytes(model)
print(f"replaced {len(replacement)} ConvInteger chains, {len(nodes)} -> {len(kept)} nodes, dropped {len(dropped)} initializers")
print(f"{src.stat().st_size} -> {dst.stat().st_size} bytes")
