[CmdletBinding()]
param([switch]$Apply, [int]$KeepDays = 2)

$ErrorActionPreference = 'Stop'
if ($KeepDays -lt 1) { throw 'KeepDays must be at least one.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cutoff = (Get-Date).ToUniversalTime().AddDays(-$KeepDays)
$roots = @('artifacts', '.perf-loop-runs', '.codex-tmp', '.tmp', 'TestResults')
$tracked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
git -C $workspace ls-files | ForEach-Object { [void]$tracked.Add($_) }
if ($LASTEXITCODE -ne 0) { throw 'Could not obtain tracked files.' }
# Retain small evidence in place; never traverse directory links/junctions.
$textExtensions = @('.md','.json','.jsonl','.csv','.tsv','.txt','.log','.xml','.trx','.yaml','.yml','.ps1','.py','.cs','.cpp','.h','.hpp','.c','.glsl','.comp','.vert','.frag','.mesh','.task','.spvasm','.html','.js','.css','.toml','.ini','.props','.targets','.csproj','.sln','.patch','.diff')
$payloadExtensions = @('.dll','.exe','.pdb','.so','.dylib','.rdc','.nsight-gfx','.nsight-gfx-gputrace','.nsight-gfx-frame','.nsight-gfx-gpucapture','.etl','.nettrace','.dmp','.dtp','.binlog','.pfm','.exr','.hdr','.png','.jpg','.jpeg','.webp','.bmp','.dds','.ktx','.ktx2','.spv','.zip','.7z','.nupkg','.bin','.cache','.pak','.gltf','.glb','.fbx','.obj','.lib','.a')
$groups = @{}
$failures = [Collections.Generic.List[string]]::new()
$freeBefore = ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($workspace))).AvailableFreeSpace
foreach ($rootName in $roots) {
    $rootPath = [IO.Path]::GetFullPath((Join-Path $workspace $rootName))
    if (-not $rootPath.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe root: $rootPath" }
    if (-not (Test-Path -LiteralPath $rootPath)) { continue }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($rootPath)
    while ($pending.Count) {
        $directory = $pending.Pop()
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            $relative = [IO.Path]::GetRelativePath($workspace, $entry.FullName).Replace('\','/')
            # Cache is operational data; pinned campaign references remain usable.
            if ($relative -eq 'artifacts/shader-cache' -or $relative -eq '.perf-loop-runs/campaign') { continue }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName); continue }
            if ($tracked.Contains($relative) -or $entry.LastWriteTimeUtc -ge $cutoff) { continue }
            $extension = $entry.Extension.ToLowerInvariant()
            if ($textExtensions -contains $extension) { continue }
            if ($payloadExtensions -notcontains $extension -and $entry.Length -lt 8MB) { continue }
            $absolute = [IO.Path]::GetFullPath($entry.FullName)
            if (-not $absolute.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe target: $absolute" }
            $parts = $relative.Split('/')
            $group = if ($parts.Length -gt 2) { $parts[0] + '/' + $parts[1] } else { $parts[0] }
            if (-not $groups.ContainsKey($group)) { $groups[$group] = [pscustomobject]@{ Path=$group; Files=0; Bytes=[long]0 } }
            $bytes = $entry.Length
            if ($Apply) {
                try { Remove-Item -LiteralPath $absolute -Force } catch { $failures.Add("${relative}: $($_.Exception.Message)"); continue }
            }
            $groups[$group].Files++
            $groups[$group].Bytes += $bytes
        }
    }
}
$records = @($groups.Values | Sort-Object Bytes -Descending)
$report = [ordered]@{
    DateUtc = (Get-Date).ToUniversalTime().ToString('o')
    Applied = [bool]$Apply
    KeepDays = $KeepDays
    TotalBytes = [long](($records | Measure-Object Bytes -Sum).Sum)
    TotalFiles = ($records | Measure-Object Files -Sum).Sum
    FreeBytesBefore = $freeBefore
    FreeBytesAfter = ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($workspace))).AvailableFreeSpace
    Groups = $records
    Failures = @($failures.ToArray())
}
if ($Apply) {
    $reportDirectory = Join-Path $workspace 'docs/performance/milestones'
    [void](New-Item -ItemType Directory -Path $reportDirectory -Force)
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory ('cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'))
}
$records | Select-Object -First 20 Path,Files,@{Name='GiB';Expression={[math]::Round($_.Bytes/1GB,2)}} | Format-Table -AutoSize
[pscustomobject]@{Applied=[bool]$Apply; Files=$report.TotalFiles; GiB=[math]::Round($report.TotalBytes/1GB,2); FreeGiB=[math]::Round($report.FreeBytesAfter/1GB,2); Failures=$failures.Count}
