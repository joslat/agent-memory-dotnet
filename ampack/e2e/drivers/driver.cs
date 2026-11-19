// SPDX-License-Identifier: Apache-2.0
// The end-to-end driver for the NuGet package: loads one pack as a user of the package does and prints what it read.
// run.py puts this file in a new console project (.NET 8) that references the package Ampack from a local feed.
//
// usage: driver <pack> <trust.json> [--revocations <file>]... [--at <RFC 3339>]
//
// Prints one line of JSON: {"verified": true, "pack": {...}} or {"verified": false, "reason": "..."}. Exit code 0 in
// both cases; anything else is a defect of the package or of this driver.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ampack;

var positional = new List<string>();
var revocations = new List<ReadOnlyMemory<byte>>();
DateTimeOffset? at = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--revocations") revocations.Add(File.ReadAllBytes(args[++i]));
    else if (args[i] == "--at") at = DateTimeOffset.Parse(args[++i], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    else positional.Add(args[i]);
}
if (positional.Count != 2)
{
    Console.WriteLine("usage: driver <pack> <trust.json> [--revocations <file>]... [--at <RFC 3339>]");
    return 2;
}

JsonObject answer;
try
{
    var pack = Pack.Load(File.ReadAllBytes(positional[0]), File.ReadAllBytes(positional[1]), new PackOptions { Revocations = revocations, At = at });
    answer = new JsonObject { ["verified"] = true, ["pack"] = Shape(pack) };
}
catch (PackRefusedException refused)
{
    answer = new JsonObject { ["verified"] = false, ["reason"] = refused.Message };
}
Console.WriteLine(answer.ToJsonString());
return 0;

static JsonObject Shape(Pack pack)
{
    var files = new JsonObject();
    foreach (var path in pack.Paths) files[path] = Convert.ToHexString(SHA256.HashData(pack.Bytes(path)!.Value.Span)).ToLowerInvariant();
    var records = new JsonArray();
    foreach (var record in pack.Records())
    {
        var links = new JsonArray();
        foreach (var link in record.Links) links.Add(new JsonArray(link.Relation, link.Target));
        records.Add(new JsonObject
        {
            ["type"] = record.Type, ["key"] = record.Key, ["properties"] = Values(record.Properties), ["links"] = links,
            ["extra"] = Values(record.Extra), ["file"] = record.File, ["line"] = record.Line,
        });
    }
    var members = new JsonArray();
    foreach (var member in pack.Members) members.Add(Shape(member));
    return new JsonObject
    {
        ["id"] = pack.Id, ["version"] = pack.Version, ["digest"] = pack.Digest, ["kind"] = pack.Kind, ["title"] = pack.Title,
        ["files"] = files, ["records"] = records, ["members"] = members,
    };
}

static JsonObject Values(IReadOnlyDictionary<string, JsonElement> values)
{
    var result = new JsonObject();
    foreach (var (name, value) in values) result[name] = JsonNode.Parse(value.GetRawText());
    return result;
}
