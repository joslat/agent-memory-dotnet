using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The licensing mode of this repository (README, "Licensing"; CHANGELOG 2.0.0, "Licensing"): the projects that implement the
/// ampack format or its host procedure are Apache-2.0, each with the canonical licence text, a NOTICE, the package licence
/// expression and an SPDX header on every source file; everything else is MIT and carries no Apache header; the README lists
/// every Apache-2.0 project and PATENTS.md holds the pledge. This test fails when a file or a project drifts from that.
/// </summary>
public sealed class LicensingGuardTests
{
    private const string ApacheSha256 = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30";

    private static readonly string[] ApacheDirectories =
    [
        "src/AgentMemory.Extensibility", "src/AgentMemory.Extensibility.Ampack", "src/AgentMemory.Extensibility.Neo4j",
        "src/AgentMemory.Extensibility.AgentFramework", "src/AgentMemory.Packs.Format", "src/AgentMemory.Packs.CodeMap",
        "src/AgentMemory.Packs.CodeMap.Roslyn", "src/AgentMemory.Packs.Coding", "src/AgentMemory.Packs.Decisions",
        "src/AgentMemory.Packs.Repository", "src/AgentMemory.Packs.Solid", "src/AgentMemory.Packs.Work", "src/AgentMemory.Packs.Intent",
        "src/AgentMemory.Packs.Architecture", "tools/Ampack", "reader-rs", "conformance/pack-v1.1",
    ];

    private static readonly string[] SourceExtensions = [".cs", ".rs", ".py", ".mjs", ".js", ".wit", ".sh"];
    private static readonly string[] SkippedDirectories = ["bin", "obj", "target", "dist", "node_modules", "__pycache__", ".git"];
    private static readonly string[] MitRoots = ["src", "tools", "tests", "samples", "packs", "crosslang", "benchmarks", "eng"];

    [Fact]
    public void Every_Apache_project_carries_the_canonical_licence_a_notice_a_package_expression_and_a_header_on_every_source_file()
    {
        var root = RepositoryRoot();
        var problems = new List<string>();
        foreach (var relative in ApacheDirectories)
        {
            var directory = Path.Combine(root, relative);
            if (!Directory.Exists(directory))
            {
                problems.Add($"{relative}: directory missing");
                continue;
            }
            var licence = Path.Combine(directory, "LICENSE");
            if (!File.Exists(licence) || Sha256(licence) != ApacheSha256)
                problems.Add($"{relative}/LICENSE: missing or not the canonical Apache-2.0 text");
            var notice = Path.Combine(directory, "NOTICE");
            if (!File.Exists(notice) || !File.ReadAllText(notice).Contains("Apache License, Version 2.0", StringComparison.Ordinal))
                problems.Add($"{relative}/NOTICE: missing or not naming the licence");
            foreach (var project in Directory.GetFiles(directory, "*.csproj"))
                if (!File.ReadAllText(project).Contains("<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>", StringComparison.Ordinal))
                    problems.Add($"{Relative(root, project)}: no Apache-2.0 package licence expression");
            foreach (var file in SourceFiles(directory))
                if (!HasApacheHeader(file))
                    problems.Add($"{Relative(root, file)}: no SPDX Apache-2.0 header on its first line");
        }
        var cargo = Path.Combine(root, "reader-rs", "Cargo.toml");
        if (!File.Exists(cargo) || !File.ReadAllText(cargo).Contains("license = \"Apache-2.0\"", StringComparison.Ordinal))
            problems.Add("reader-rs/Cargo.toml: the workspace licence is not Apache-2.0");
        string.Join(Environment.NewLine, problems).Should().BeEmpty();
    }

    [Fact]
    public void No_MIT_project_carries_an_Apache_header_and_the_README_and_the_pledge_name_the_mode()
    {
        var root = RepositoryRoot();
        var problems = new List<string>();
        var apache = ApacheDirectories.Select(d => Path.GetFullPath(Path.Combine(root, d)) + Path.DirectorySeparatorChar).ToArray();
        foreach (var top in MitRoots)
        {
            var directory = Path.Combine(root, top);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in SourceFiles(directory))
            {
                if (apache.Any(a => file.StartsWith(a, StringComparison.OrdinalIgnoreCase))) continue;
                if (HasApacheHeader(file))
                    problems.Add($"{Relative(root, file)}: Apache-2.0 header on a file outside the Apache-2.0 projects (the rest is MIT)");
            }
        }
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var start = readme.IndexOf("## Licensing", StringComparison.Ordinal);
        if (start < 0)
        {
            problems.Add("README.md: no Licensing section");
        }
        else
        {
            var licensing = readme[start..];
            foreach (var relative in ApacheDirectories)
            {
                var covered = licensing.Contains(relative, StringComparison.Ordinal)
                    || (relative.StartsWith("src/AgentMemory.Packs.", StringComparison.Ordinal) && licensing.Contains("src/AgentMemory.Packs.*", StringComparison.Ordinal));
                if (!covered) problems.Add($"README.md Licensing: {relative} is Apache-2.0 but not listed");
            }
        }
        var map = Path.Combine(root, "LICENSING.md");
        if (!File.Exists(map))
        {
            problems.Add("LICENSING.md: missing (the complete licence map of the repository)");
        }
        else
        {
            var text = File.ReadAllText(map);
            foreach (var relative in ApacheDirectories)
            {
                var covered = text.Contains(relative, StringComparison.Ordinal);
                if (!covered) problems.Add($"LICENSING.md: {relative} is Apache-2.0 but not in the map");
            }
            if (!text.Contains("PATENTS.md", StringComparison.Ordinal) || !text.Contains("CC BY 4.0", StringComparison.Ordinal) || !text.Contains("MIT", StringComparison.Ordinal))
                problems.Add("LICENSING.md: must name MIT, CC BY 4.0 and PATENTS.md");
        }
        var patents = Path.Combine(root, "PATENTS.md");
        if (!File.Exists(patents) || !File.ReadAllText(patents).Contains("## The pledge", StringComparison.Ordinal))
            problems.Add("PATENTS.md: missing or without the pledge section");
        string.Join(Environment.NewLine, problems).Should().BeEmpty();
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => SkippedDirectories.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            if (SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) yield return file;
        }
    }

    // The first line (after a BOM, and after a shebang line) must be the SPDX comment.
    private static bool HasApacheHeader(string file)
    {
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(file)).TrimStart('﻿');
        var first = FirstLine(text);
        if (first.StartsWith("#!", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            first = newline < 0 ? string.Empty : FirstLine(text[(newline + 1)..]);
        }
        return first.Contains("SPDX-License-Identifier: Apache-2.0", StringComparison.Ordinal);
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).TrimEnd('\r');
    }

    private static string Sha256(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds AgentMemory.slnx.");
    }
}
