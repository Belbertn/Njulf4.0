# Shared by campaign and standalone experiment verification, including frozen-comparer workflows.
function Assert-LoadedShaderIdentity {
    param($Identity, [string]$Label = 'Capture')
    if ($null -eq $Identity -or
        [string]$Identity.Schema -cne 'njulf-loaded-shaders/v1') {
        throw "$Label loaded shader identity is missing or unsupported; recapture is required."
    }
    $modules = @($Identity.Modules)
    if ($null -eq $Identity.Modules -or $modules.Count -eq 0 -or
        [string]$Identity.Generation -cne [string]$modules.Count -or
        $null -eq $Identity.FailureReason -or [string]$Identity.FailureReason -cne '') {
        throw "$Label loaded shader inventory is empty, conflicting, or has an invalid generation."
    }
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        $schemaBytes = [Text.Encoding]::UTF8.GetBytes('njulf-loaded-shaders/v1')
        $writer.Write([int]$schemaBytes.Length)
        $writer.Write($schemaBytes)
        $writer.Write([int]$modules.Count)
        $previous = $null
        foreach ($module in $modules) {
            $name = [string]$module.FileName
            if ($name.Length -eq 0 -or $name.Length -gt 512 -or
                $name -cnotmatch '^[^\\/:*?"<>|\x00-\x1f]+\.spv$' -or
                [string]$module.Sha256 -cnotmatch '^[0-9a-f]{64}$' -or
                [string]$module.ByteLength -cnotmatch '^[1-9][0-9]*$' -or
                [long]$module.ByteLength -gt 16777216 -or [long]$module.ByteLength % 4 -ne 0 -or
                [string]$module.SourceKind -cnotin @('embedded', 'override', 'deployment') -or
                [string]::IsNullOrWhiteSpace([string]$module.SourceIdentity) -or
                ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $name) -ge 0)) {
                throw "$Label has a noncanonical or conflicting loaded shader module."
            }
            $previous = $name
            $nameBytes = [Text.Encoding]::UTF8.GetBytes($name)
            $writer.Write([int]$nameBytes.Length)
            $writer.Write($nameBytes)
            $writer.Write([int]$module.ByteLength)
            $writer.Write([Convert]::FromHexString([string]$module.Sha256))
        }
        $writer.Flush()
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $expected = 'sha256:' + [Convert]::ToHexString($hash.ComputeHash($stream.ToArray())).ToLowerInvariant() }
        finally { $hash.Dispose() }
        if ([string]$Identity.Fingerprint -cne $expected) {
            throw "$Label loaded shader fingerprint does not match its inventory."
        }
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Assert-LoadedShaderMeasurement {
    param($Report, [string]$Label = 'Benchmark')
    $property = $Report.LastDiagnostics.CaptureRun.PSObject.Properties['LoadedShaderIdentity']
    if ($null -eq $property) { throw "$Label lacks loaded shader evidence; recapture is required." }
    $identity = $property.Value
    Assert-LoadedShaderIdentity $identity $Label
    $boundaryProperty = $Report.CaptureContract.PSObject.Properties['LoadedShaders']
    if ($null -eq $boundaryProperty) { throw "$Label lacks loaded shader measurement boundaries; recapture is required." }
    $boundary = $boundaryProperty.Value
    if ($null -eq $boundary -or
        [string]$boundary.StartFingerprint -cne [string]$identity.Fingerprint -or
        [string]$boundary.EndFingerprint -cne [string]$identity.Fingerprint -or
        [string]$boundary.StartGeneration -cne [string]$identity.Generation -or
        [string]$boundary.EndGeneration -cne [string]$identity.Generation) {
        throw "$Label loaded shader measurement boundaries are missing or changed; recapture is required."
    }
}

function Assert-LoadedShaderCaptureSeries {
    param([object[]]$Reports, [bool]$SameAcrossRoles = $false)
    $roles = @($null, $null)
    for ($index = 0; $index -lt $Reports.Count; $index++) {
        $report = $Reports[$index]
        Assert-LoadedShaderMeasurement $report "Capture $index"
        $identity = $report.LastDiagnostics.CaptureRun.LoadedShaderIdentity
        $role = if (($index % 4) -in @(0, 3)) { 0 } else { 1 }
        if ($null -ne $roles[$role]) {
            Assert-LoadedShaderPair $roles[$role] $identity $true "Repeated role $role"
        }
        $roles[$role] = $identity
    }
    if ($SameAcrossRoles) { Assert-LoadedShaderPair $roles[0] $roles[1] $true 'A/A roles' }
}

function Assert-LoadedShaderPair {
    param($Left, $Right, [bool]$RequireSameInventory, [string]$Label = 'Pair')
    Assert-LoadedShaderIdentity $Left "$Label left"
    Assert-LoadedShaderIdentity $Right "$Label right"
    if ($RequireSameInventory) {
        if ([string]$Left.Fingerprint -cne [string]$Right.Fingerprint) {
            throw "$Label loaded shader identities differ."
        }
        return
    }
    $modules = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($module in $Left.Modules) { $modules.Add([string]$module.FileName, $module) }
    foreach ($module in $Right.Modules) {
        if ($modules.ContainsKey([string]$module.FileName)) {
            $expected = $modules[[string]$module.FileName]
            if ([string]$expected.Sha256 -cne [string]$module.Sha256 -or
                [int]$expected.ByteLength -ne [int]$module.ByteLength) {
                throw "$Label loaded shader '$($module.FileName)' differs between feature-isolation variants."
            }
        }
    }
}
