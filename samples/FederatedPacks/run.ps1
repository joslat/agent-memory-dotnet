#Requires -Version 7
<#
.SYNOPSIS
  Federated memory packs, end to end, on a local Solid server: a supplier publishes a signed pack for one buyer; the
  buyer's host discovers it from the supplier's WebID, verifies it and installs it; a stranger gets nothing.

.DESCRIPTION
  Needs Docker and the .NET 10 SDK. Starts the server in compose.yaml (test accounts only, in memory), builds ampack,
  and runs every step with it. Client credentials are registered on the local server and passed to ampack through the
  environment of each call only. -Access acp runs the server with Access Control Policies instead of Web Access Control;
  the pack, the receipt and every check are the same. Use -KeepServer to leave the server running afterwards.
#>
param([ValidateSet('wac', 'acp')] [string] $Access = 'wac', [switch] $KeepServer)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '../..')).Path
$base = 'http://localhost:3000'
$webId = @{ supplier = "$base/supplier/profile/card#me"; buyer = "$base/buyer/profile/card#me"; stranger = "$base/stranger/profile/card#me" }
$work = Join-Path ([IO.Path]::GetTempPath()) "federated-packs-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory $work | Out-Null

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# ampack as a host or a publisher runs it; -As logs it in with that account's client credentials (and -Probe a second one).
function Ampack {
    [CmdletBinding(PositionalBinding = $false)]
    param([string] $As, [string] $Probe, [switch] $MayRefuse, [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    $saved = @{}
    foreach ($name in 'AMPACK_SOLID_CLIENT_ID', 'AMPACK_SOLID_CLIENT_SECRET', 'AMPACK_PROBE_CLIENT_ID', 'AMPACK_PROBE_CLIENT_SECRET') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    try {
        if ($As) { $env:AMPACK_SOLID_CLIENT_ID = $credentials[$As].id; $env:AMPACK_SOLID_CLIENT_SECRET = $credentials[$As].secret }
        if ($Probe) { $env:AMPACK_PROBE_CLIENT_ID = $credentials[$Probe].id; $env:AMPACK_PROBE_CLIENT_SECRET = $credentials[$Probe].secret }
        & dotnet $ampack @Arguments
        if ($LASTEXITCODE -ne 0 -and -not ($MayRefuse -and $LASTEXITCODE -eq 1)) { throw "ampack $($Arguments[0]) failed ($LASTEXITCODE)." }
    }
    finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
}

Push-Location $work
try {
    Step "Start a local Solid server with three test accounts (access control: $Access)"
    $env:SOLID_CONFIG = if ($Access -eq 'acp') { 'config/file-acp.json' } else { 'config/default.json' }
    docker compose -f (Join-Path $here 'compose.yaml') up -d --force-recreate | Out-Null
    for ($i = 0; $i -lt 60; $i++) { try { Invoke-WebRequest "$base/" -Method Head | Out-Null; break } catch { Start-Sleep -Seconds 1 } }

    Step 'Register client credentials for each account (as an operator would, once)'
    $credentials = @{}
    foreach ($name in 'supplier', 'buyer', 'stranger') {
        $token = (Invoke-RestMethod "$base/.account/login/password/" -Method Post -ContentType 'application/json' -Body (@{ email = "$name@example.test"; password = "$name-test-only" } | ConvertTo-Json)).authorization
        $headers = @{ Authorization = "CSS-Account-Token $token" }
        $controls = (Invoke-RestMethod "$base/.account/" -Headers $headers).controls.account
        $credentials[$name] = Invoke-RestMethod $controls.clientCredentials -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ name = "ampack-$name"; webId = $webId[$name] } | ConvertTo-Json)
        Write-Host "  $name -> $($webId[$name])"
    }

    Step 'Build ampack'
    dotnet build (Join-Path $repo 'tools/Ampack') -c Release -nologo -v:q | Out-Null
    $ampack = Join-Path $repo 'tools/Ampack/bin/Release/net10.0/Ampack.dll'

    Step 'The supplier makes a signing key and asserts it in its WebID profile'
    Ampack keygen --out keys --name supplier --prefix io.supplier | Out-Null
    Ampack -As supplier solid bind-key --webid $webId.supplier --public keys/supplier.pub.pem --allow-loopback-http

    Step "The buyer's host reads the supplier's profile and binds the key it checked (never trust on first use)"
    Ampack trust webid $webId.supplier --allow-loopback-http
    $keyId = ((Ampack trust webid $webId.supplier --allow-loopback-http) | Select-String '^\s+([0-9a-f]{64})').Matches[0].Groups[1].Value
    Ampack trust webid $webId.supplier --key $keyId --policy buyer-trust.json --prefix io.supplier --host-webid $webId.buyer --allow-loopback-http
    Ampack trust webid $webId.supplier --key $keyId --policy stranger-trust.json --prefix io.supplier --host-webid $webId.stranger --allow-loopback-http | Out-Null

    Step 'The supplier writes a knowledge pack for the buyer alone, and builds and signs it'
    # --publisher and --audience make it federated: who publishes it and who it is for, inside what is signed.
    Ampack init handoff --id io.supplier.handoff --version 1.0.0 --publisher $webId.supplier --audience $webId.buyer --purpose 'Supply-chain handoff for PO-123' | Out-Null
    $pack = Get-Content handoff/pack.json -Raw | ConvertFrom-Json -AsHashtable
    $pack.display = @{ title = 'PO-123 handoff'; description = 'What the buyer needs to know about purchase order PO-123.' }
    [IO.File]::WriteAllText((Join-Path $work 'handoff/pack.json'), ($pack | ConvertTo-Json -Depth 10 -Compress))
    $notes = @(
        @{ type = 'HandoffNote'; key = 'po-123-delivery'; properties = @{ title = 'PO-123 delivery'; text = 'Confirmed for 15 November 2026 at the Basel dock, gate 4.' } },
        @{ type = 'HandoffNote'; key = 'po-123-contact'; properties = @{ title = 'PO-123 contact'; text = 'The supplier''s logistics desk handles changes until two days before delivery.' } },
        @{ type = 'HandoffNote'; key = 'po-123-pallets'; properties = @{ title = 'PO-123 pallets'; text = 'Twelve pallets, euro size, stackable two high.' } }
    )
    [IO.File]::WriteAllText((Join-Path $work 'handoff/records/note.jsonl'), (($notes | ForEach-Object { $_ | ConvertTo-Json -Depth 5 -Compress }) -join "`n") + "`n")
    # The pack's own test: the buyer's host runs it after install, offline.
    New-Item -ItemType Directory handoff/evals | Out-Null
    [IO.File]::WriteAllText((Join-Path $work 'handoff/evals/tests.json'),
        '{"format":"agentmemory.pack.tests/1.2","tests":[{"name":"when PO-123 arrives","view":"notes","search":"PO-123 delivery dock","expect":["po-123-delivery"],"top":3}]}')
    Ampack build handoff --out handoff.ampack --key keys/supplier.key.pem

    Step 'The supplier publishes it in its Pod: access for the buyer, a probe, a signed receipt, a catalog'
    Ampack -As supplier -Probe buyer solid publish handoff.ampack --webid $webId.supplier --key keys/supplier.key.pem --probe-as $webId.buyer --allow-loopback-http

    Step 'Who sees what, from the supplier''s WebID alone'
    Write-Host '  the buyer:'; Ampack -As buyer solid discover --from $webId.supplier --webid $webId.buyer --allow-loopback-http
    Write-Host '  the stranger:'; Ampack -As stranger solid discover --from $webId.supplier --webid $webId.stranger --allow-loopback-http
    Write-Host '  anyone:'; Ampack solid discover --from $webId.supplier --allow-loopback-http

    Step "The buyer's host fetches, re-checks the key, verifies pack and receipt, and installs"
    Ampack -As buyer solid fetch --from $webId.supplier --webid $webId.buyer --policy buyer-trust.json --store buyer-store --allow-loopback-http
    Ampack list --store buyer-store
    Ampack test --store buyer-store --policy buyer-trust.json
    Write-Host "  where it came from (buyer-store/federation.jsonl):"
    Get-Content buyer-store/federation.jsonl | ForEach-Object { Write-Host "  $_" }

    Step "The stranger's host gets nothing"
    Ampack -As stranger -MayRefuse solid fetch --from $webId.supplier --webid $webId.stranger --policy stranger-trust.json --store stranger-store --allow-loopback-http
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    if (-not $KeepServer) { docker compose -f (Join-Path $here 'compose.yaml') down | Out-Null }
}
