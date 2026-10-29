"""Differential fuzzing of the two pack verifiers (review 1, I5).

Feeds the same mutated packs to `ampack verify` (.NET) and python/ampack_verify.py and reports every input on which they
disagree. Nothing is expected of a mutant except that the two verifiers reach the same verdict: a disagreement is a bug in
one of them, or a gap in the spec. Two kinds of mutant, from a seeded generator so a run can be repeated:

- archive mutants: bytes of a valid pack flipped, inserted, removed or truncated (the ZIP readers);
- content mutants: one JSON or JSONL file of a valid pack edited (a character changed, inserted or removed, a key repeated,
  a number or string swapped for an edge case), then listed and signed again, so the mutant reaches the JSON profile, the
  manifest and the kind rules instead of stopping at a hash;
- time mutants (review 2): pack.json's validUntil written in edge forms (fractions of 0 to 9 digits, offsets up to and past
  14 hours, minutes of 60, lower-case markers, spaces), signed again;
- path mutants: a file added under an edge path (case twins of folders, a file named like a folder, device names, trailing
  dots, depth 8 and 9, long segments), signed again;
- envelope mutants: the signature envelope's JSON edited (fields added, key ids and signatures of odd shapes, spacing);
- policy mutants: the trust policy edited (unknown fields at each level, key PEMs that are private or doubled or CRLF, odd
  prefixes and times), the pack unchanged.

usage: python differential.py <path to Ampack executable> [<out folder>] [--count N] [--seed S]
Run run_vectors.py first: it writes the valid pack this starts from. Writes differential-results.json; exits 1 on any
disagreement.
"""
import hashlib
import json
import random
import subprocess
import sys
import tempfile
from pathlib import Path

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE / "python"))


def option(name, default):
    if name in sys.argv:
        i = sys.argv.index(name)
        value = sys.argv[i + 1]
        del sys.argv[i:i + 2]
        return int(value)
    return default


COUNT = option("--count", 300)
SEED = option("--seed", 20261029)

import ampack_verify as py  # noqa: E402
import run_vectors as kit  # noqa: E402  (reads the same arguments: the Ampack executable and the out folder)

# Edge cases a JSON reader may take differently: lone surrogates, a NUL, numbers at and past the profile's limits, a BOM,
# a non-NFC string, a CR, deep nesting.
EDGES = [b'"\\ud800"', b'"\\u0000"', b"9007199254740993", b"1e400", b"-0", b"0.1e-400", b'"\xef\xbb\xbf"', b'"e\xcc\x81"',
         b'"\r"', b"[" * 70 + b"]" * 70, b"true", b"null", b'{"a":1,"a":2}', b"1.0", b"01"]
INSERTS = b'{}[],:"\\\x00\r\n\t \xff\xc3\xa9'


def mutate_bytes(data, rng):
    data = bytearray(data)
    for _ in range(rng.randint(1, 3)):
        op = rng.choice(("flip", "insert", "remove", "truncate"))
        if not data:
            break
        i = rng.randrange(len(data))
        if op == "flip":
            data[i] ^= 1 << rng.randrange(8)
        elif op == "insert":
            data[i:i] = bytes([rng.choice(INSERTS)])
        elif op == "remove":
            del data[i]
        else:
            del data[rng.randrange(len(data)):]
    return bytes(data)


def mutate_json(data, rng):
    op = rng.choice(("bytes", "edge", "repeat-key"))
    if op == "bytes":
        return mutate_bytes(data, rng)
    text = data
    if op == "edge":
        # Swap one scalar value (after a colon) for an edge case.
        colons = [i for i, c in enumerate(text) if c == ord(":")]
        if not colons:
            return mutate_bytes(data, rng)
        start = rng.choice(colons) + 1
        end = start
        while end < len(text) and text[end] not in b",}]\n":
            end += 1
        return text[:start] + rng.choice(EDGES) + text[end:]
    # Repeat the first key of an object.
    brace = text.find(b"{")
    quote = text.find(b'"', brace + 1)
    close = text.find(b'"', quote + 1)
    if brace < 0 or quote < 0 or close < 0:
        return mutate_bytes(data, rng)
    return text[:brace + 1] + text[quote:close + 1] + b':"x",' + text[brace + 1:]


TIMES = ["2099-01-01T00:00:00Z", "2099-01-01T00:00:00.5Z", "2099-01-01T00:00:00.1234567Z", "2099-01-01T00:00:00.12345678Z",
         "2099-01-01T00:00:00.5+01:00", "2099-01-01T00:00:00-05:30", "2099-01-01T00:00:00+14:00", "2099-01-01T00:00:00+14:01",
         "2099-01-01T00:00:00+05:60", "2099-01-01t00:00:00z", "2099-01-01T00:00:00", "2099-01-01 00:00:00Z", " 2099-01-01T00:00:00Z",
         "2099-02-30T00:00:00Z", "2099-01-01T24:00:00Z", "2099-01-01T00:00:60Z", "9999-12-31T23:59:59.9999999Z", "0001-01-01T00:00:00Z",
         "2020-01-01T00:00:00.000000001+00:00", "2099-01-01T00:00:00.Z", "2099-01-01T00:00:00,5Z"]
PATHS = ["assets/Icons/x.png|assets/icons/y.png", "notes|notes/a.json", "records/CON.jsonl", "records/a..jsonl", "records/a.jsonl.",
         "a/b/c/d/e/f/g/h.json", "a/b/c/d/e/f/g/h/i.json", "records/" + "x" * 100 + ".jsonl", "records/" + "x" * 101 + ".jsonl",
         "Records/z.jsonl", "META/extra.json", "records/a b.jsonl", ".hidden/x.json", "records/-x.jsonl"]


def mutate_envelope(envelope, rng):
    env = json.loads(envelope)
    op = rng.choice(("field", "keyid", "sig", "spacing", "entry", "type"))
    if op == "field":
        env[rng.choice(("note", "payloadtype", "signature"))] = "x"
    elif op == "keyid":
        env["signatures"][0]["keyid"] = rng.choice((env["signatures"][0]["keyid"].upper(), env["signatures"][0]["keyid"][:63], "x" * 64, 7))
    elif op == "sig":
        sig = env["signatures"][0]["sig"]
        env["signatures"][0]["sig"] = rng.choice((sig + "=", sig.rstrip("="), sig[:-4], "", "A" * 100, sig.replace("+", "-")))
    elif op == "entry":
        env["signatures"].append(rng.choice(({"keyid": "0" * 64, "sig": "AAAA"}, {"keyid": "0" * 64}, {"keyid": "0" * 64, "sig": "AAAA", "x": 1})))
    elif op == "type":
        env["payloadType"] = rng.choice(("application/vnd.in-toto+JSON", "application/json", ""))
    else:
        return envelope.replace(b'":"', b'": "', 1)
    return json.dumps(env, separators=(",", ":")).encode()


def mutate_policy(policy_doc, rng, private_pem):
    doc = json.loads(json.dumps(policy_doc))
    key = doc["keys"][0]
    op = rng.choice(("top", "key", "pem", "prefix", "time", "roles"))
    if op == "top":
        doc[rng.choice(("requiredroles", "Keys", "note"))] = []
    elif op == "key":
        key[rng.choice(("notafter", "idprefixes", "webid", "x"))] = "x"
    elif op == "pem":
        key["publicKey"] = rng.choice((private_pem, key["publicKey"] + key["publicKey"], key["publicKey"].replace("\n", "\r\n"),
                                       key["publicKey"].strip(), " " + key["publicKey"], key["publicKey"].replace("PUBLIC", "public")))
    elif op == "prefix":
        key["idPrefixes"] = [rng.choice(("io.Example", "io.example.", "io..example", "", "io.example\n", "io", "io.example.notes"))]
    elif op == "time":
        key[rng.choice(("notBefore", "notAfter"))] = rng.choice(TIMES)
    else:
        key["roles"] = [rng.choice(("Publisher", "publisher", "revoker", "evaluator", "assurer", ""))]
    return doc


def verdict(code):
    return {0: "verified", 1: "refused"}.get(code, f"error ({code})")


def main():
    seed_pack = kit.VECTORS / "valid-dotnet.ampack"
    if not seed_pack.exists():
        print(f"No {seed_pack}: run run_vectors.py first.")
        return 2
    rng = random.Random(SEED)
    base = py.read_zip(seed_pack.read_bytes())
    content = {p: b for p, b in base.items() if p != py.FILES and not p.startswith(py.SIGNATURES)}
    listing = json.loads(base[py.FILES])
    pid, version = listing["id"], listing["version"]
    key = ec.generate_private_key(ec.SECP256R1())
    spki = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    pem = key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
    work = Path(tempfile.mkdtemp(prefix="ampack-differential-"))
    policy = work / "trust.json"
    policy.write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [{
        "keyId": hashlib.sha256(spki).hexdigest(), "publicKey": pem, "idPrefixes": ["io.example"], "roles": ["publisher"]}]}), encoding="utf-8")

    def signed(files):
        lb = kit.files_list(pid, version, files)
        path, env = kit.sign(lb, pid, version, key)
        return kit.write_zip({**files, py.FILES: lb, path: env})

    valid = signed(content)
    parseable = sorted(p for p in content if p.endswith((".json", ".jsonl")))
    policy_doc = json.loads(policy.read_text(encoding="utf-8"))
    private_pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode()
    valid_files = py.read_zip(valid)
    envelope_name = next(n for n in valid_files if n.startswith(py.SIGNATURES))
    results, disagreements = [], 0
    for n in range(COUNT):
        mutant_policy = policy
        kind = ("archive", "content", "time", "path", "envelope", "policy")[n % 6]
        target = None
        if kind == "archive":
            archive = mutate_bytes(valid, rng)
        elif kind == "content":
            target = rng.choice(parseable)
            archive = signed({**content, target: mutate_json(content[target], rng)})
        elif kind == "time":
            target = rng.choice(TIMES)
            manifest = json.loads(content["pack.json"])
            archive = signed({**content, "pack.json": (json.dumps({**manifest, "validUntil": target}, indent=2) + chr(10)).encode()})
        elif kind == "path":
            target = rng.choice(PATHS)
            extra = {name: (b'{"key":"z"}\n' if name.endswith(".jsonl") else b'{}\n' if name.endswith(".json") else b"x") for name in target.split("|")}
            archive = signed({**content, **extra})
        elif kind == "envelope":
            target = envelope_name
            archive = kit.write_zip({**valid_files, envelope_name: mutate_envelope(valid_files[envelope_name], rng)})
        else:
            mutant_policy = work / f"policy-{n:04d}.json"
            mutant_policy.write_text(json.dumps(mutate_policy(policy_doc, rng, private_pem)), encoding="utf-8")
            archive = valid
        path = work / f"mutant-{n:04d}.ampack"
        path.write_bytes(archive)
        dn_code, dn_text = kit.ampack("verify", str(path), "--policy", str(mutant_policy))
        pr = subprocess.run([sys.executable, "-I", str(HERE / "python" / "ampack_verify.py"), str(path), str(mutant_policy)],
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
        dotnet, python = verdict(dn_code), verdict(pr.returncode)
        agree = dotnet == python and not dotnet.startswith("error")
        if not agree:
            disagreements += 1
            kept = HERE / "differential" / f"mutant-{n:04d}.ampack"
            kept.parent.mkdir(exist_ok=True)
            kept.write_bytes(archive)
            if mutant_policy != policy:
                kept.with_suffix(".policy.json").write_bytes(mutant_policy.read_bytes())
            print(f"DISAGREE {n:4} {kind:7} {target or '':24} dotnet {dotnet:9} python {python}")
            print(f"         dotnet: {dn_text.splitlines()[0] if dn_text else ''}")
            print(f"         python: {(pr.stdout.strip() or pr.stderr.strip()[-200:]).splitlines()[-1] if (pr.stdout.strip() or pr.stderr.strip()) else ''}")
        results.append({"mutant": n, "kind": kind, "target": target, "dotnet": dotnet, "python": python, "agree": agree,
                         "sha256": hashlib.sha256(archive).hexdigest()})
    verified = sum(r["dotnet"] == "verified" and r["agree"] for r in results)
    by_kind = {k: {"count": sum(r["kind"] == k for r in results), "agreed": sum(r["kind"] == k and r["agree"] for r in results)}
               for k in ("archive", "content", "time", "path", "envelope", "policy")}
    (kit.OUT / f"differential-results-seed-{SEED}.json").write_text(json.dumps({
        "format": "agentmemory.pack.differential/1", "seed": SEED, "count": COUNT, "agreed": COUNT - disagreements,
        "bothVerified": verified, "byKind": by_kind, "mutants": results}, indent=1) + "\n", encoding="utf-8")
    print(f"{COUNT - disagreements}/{COUNT} mutants: both verifiers reach the same verdict ({verified} verified by both); seed {SEED}")
    for f in work.iterdir():
        f.unlink()
    work.rmdir()
    return 1 if disagreements else 0


if __name__ == "__main__":
    sys.exit(main())
