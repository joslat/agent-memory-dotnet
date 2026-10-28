#Requires -Version 7
<#
.SYNOPSIS
  Certified memory packs, end to end: a publisher ships a pack with its card; an independent evaluator runs the pack's
  declared trial on its own tasks and signs a certificate that travels beside the pack with the AEF run it summarises; a
  host verifies the certificate and decides, namespace by namespace, whether to rely on it or on its own trial.

.DESCRIPTION
  Needs Docker and the .NET 10 SDK. The evaluator's trial is the code map's declared test-impact trial on this repository's
  recent history, run by `agentmemory evaluate --gate` against a throwaway Neo4j container: its password is random, reaches
  the container through an environment file that is deleted afterwards, and reaches the CLI through its environment only.
  Every other step is `ampack`, offline. With -AgentEval <command>, the evaluator also seals the AEF run with AgentEval
  (`aef seal`) before certifying; the seal travels beside the run, outside its run hash. -KeepWork leaves the work folder.
#>
param([string] $AgentEval, [switch] $KeepWork)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '../..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) "certified-packs-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$container = "certified-packs-neo4j-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$port = 7698
New-Item -ItemType Directory $work | Out-Null

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Ampack {
    [CmdletBinding(PositionalBinding = $false)]
    param([switch] $MayRefuse, [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    & dotnet $ampack @Arguments
    if ($LASTEXITCODE -ne 0 -and -not ($MayRefuse -and $LASTEXITCODE -eq 1)) { throw "ampack $($Arguments[0]) failed ($LASTEXITCODE)." }
}

# The agentmemory CLI against the throwaway Neo4j: the password goes through this call's environment only.
function AgentMemory {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    $saved = @{}
    foreach ($name in 'NEO4J_URI', 'NEO4J_USERNAME', 'NEO4J_PASSWORD') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    try {
        $env:NEO4J_URI = "bolt://localhost:$port"; $env:NEO4J_USERNAME = 'neo4j'; $env:NEO4J_PASSWORD = $password
        & dotnet $cli @Arguments 2>&1 | Where-Object { $_ -notmatch '^(info|warn|dbug):|^      ' }
        if ($LASTEXITCODE -ne 0) { throw "agentmemory $($Arguments[0]) failed ($LASTEXITCODE)." }
    }
    finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
}

# The trust-policy entry ampack keygen prints for a new key.
function Entry($output) { (($output -join "`n") -replace '(?s)^.*Trust-policy entry:', '') | ConvertFrom-Json -AsHashtable }

Push-Location $work
try {
    Step 'Build ampack and the agentmemory CLI'
    dotnet build (Join-Path $repo 'tools/Ampack') -c Release -nologo -v:q | Out-Null
    dotnet build (Join-Path $repo 'tools/AgentMemory.Cli') -c Release -nologo -v:q | Out-Null
    $ampack = Join-Path $repo 'tools/Ampack/bin/Release/net10.0/Ampack.dll'
    $cli = Join-Path $repo 'tools/AgentMemory.Cli/bin/Release/net10.0/AgentMemory.Cli.dll'

    Step 'Keys: the publisher, an independent evaluator (role evaluator, that role alone), and a host key for each side'
    $publisher = Entry (Ampack keygen --out keys --name publisher --prefix io.agentmemory)
    $evaluator = Entry (Ampack keygen --out keys --name evaluator --prefix io.agentmemory --role evaluator)
    Ampack keygen --out keys --name lab-host --prefix io.agentmemory | Out-Null
    Ampack keygen --out keys --name host --prefix io.agentmemory | Out-Null
    # The host's trust policy: who may publish io.agentmemory packs, and who may certify them.
    [IO.File]::WriteAllText((Join-Path $work 'trust.json'), (@{ format = 'agentmemory.pack.trust/1.1'; keys = @($publisher, $evaluator) } | ConvertTo-Json -Depth 5 -Compress))
    Write-Host "  publisher $($publisher.keyId.Substring(0, 16))  evaluator $($evaluator.keyId.Substring(0, 16))"

    Step 'The publisher ships the code map definition as a signed pack, with its card'
    Ampack migrate (Join-Path $repo 'src/AgentMemory.Packs.CodeMap/manifest.json') --out codemap | Out-Null
    $card = [ordered]@{
        format = 'agentmemory.pack.card/1'
        owner = [ordered]@{ name = 'AgentMemory maintainers'; contact = 'https://github.com/joslat/agent-memory-dotnet' }
        intendedUse = 'Offer the tests a code change touches, from a graph of the repository''s code, to a coding agent.'
        limitations = 'C# repositories indexed by the code map; it offers candidates, not a test plan. Not for other languages yet.'
        boundaries = @('Do not use it to skip running the test suite.')
        risks = @('A stale code map offers tests for code that has moved.')
        provenance = 'Declarations only: the host indexes its own repository.'
    }
    [IO.File]::WriteAllText((Join-Path $work 'codemap/card.json'), ($card | ConvertTo-Json -Depth 5 -Compress))
    Ampack build codemap --out codemap.ampack --key keys/publisher.key.pem
    Ampack verify codemap.ampack --policy trust.json

    Step 'The evaluator runs the pack''s declared trial on its own tasks (this repository''s recent commits, Neo4j in a throwaway container)'
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
    $envFile = Join-Path $work 'neo4j.env'
    [IO.File]::WriteAllText($envFile, "NEO4J_AUTH=neo4j/$password`nNEO4J_server_memory_heap_max__size=1g`nNEO4J_server_memory_pagecache_size=256m`n")
    try { docker run -d --name $container -p "${port}:7687" --env-file $envFile neo4j:5.26 | Out-Null }
    finally { Remove-Item $envFile -Force }
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $container sh -c 'cypher-shell -u neo4j -p "${NEO4J_AUTH#neo4j/}" "RETURN 1"' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 2
    }
    AgentMemory bootstrap | Select-Object -Last 1
    AgentMemory codemap import --root $repo | Out-Null
    Write-Host '  this repository''s code map imported'
    AgentMemory evaluate --gate io.agentmemory.packs.codemap --repo $repo --pack codemap.ampack --trust trust.json --host-key keys/lab-host.key.pem --activations lab-decisions
    docker rm -f $container | Out-Null

    Step 'The evaluator turns its signed decision into an AEF run and signs a certificate that travels beside the pack'
    Ampack evidence --activations lab-decisions --host-key keys/lab-host.pub.pem --pack io.agentmemory.packs.codemap --out run
    if ($AgentEval) { & $AgentEval aef seal run --sealed-by ingest; if ($LASTEXITCODE -ne 0) { throw 'aef seal failed.' } }
    Ampack certify codemap.ampack --policy trust.json --activations lab-decisions --activation-key keys/lab-host.pub.pem `
        --key keys/evaluator.key.pem --evaluator 'Example Lab' --task-source 'recent commits of agent-memory-dotnet' --aef run --out codemap.certified.ampack

    Step 'A host reads it: by a key it names as evaluator, for exactly this pack, with the run it summarises; the digest is unchanged'
    Ampack certificate verify codemap.certified.ampack --policy trust.json
    Ampack verify codemap.certified.ampack --policy trust.json

    Step 'The host records a decision from the certificate, signed with its own key'
    Ampack admit codemap.certified.ampack --policy trust.json --host-key keys/host.key.pem --activations host-decisions

    Step 'Proven here is the default: the certificate counts only where this host trusts certificates for the namespace'
    Write-Host '  default (proven here):'
    Ampack gate codemap.certified.ampack --policy trust.json --activations host-decisions --host-key keys/host.pub.pem
    Write-Host '  TrustCertificatesFor io.agentmemory:'
    Ampack gate codemap.certified.ampack --policy trust.json --activations host-decisions --host-key keys/host.pub.pem --trust-certificates-for io.agentmemory

    Step 'A host that does not name the evaluator still sees the certificate, untrusted, and admits nothing on it'
    [IO.File]::WriteAllText((Join-Path $work 'publisher-only.json'), (@{ format = 'agentmemory.pack.trust/1.1'; keys = @($publisher) } | ConvertTo-Json -Depth 5 -Compress))
    Ampack verify codemap.certified.ampack --policy publisher-only.json
    Ampack -MayRefuse admit codemap.certified.ampack --policy publisher-only.json --host-key keys/host.key.pem --activations other-decisions
}
finally {
    Pop-Location
    docker rm -f $container 2>$null | Out-Null
    if ($KeepWork) { Write-Host "`nWork folder: $work (holds private keys: delete it when done)" }
    else { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
