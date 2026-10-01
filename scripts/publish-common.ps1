<#
    Shared by install.ps1 and package.ps1: one Release build in artifacts\publish, rebuilt only when
    the source has changed since it was made.

    The build is stamped with a fingerprint of the source files (a git tree of the working folder,
    committed or not). When it matches, the existing build is reused, which turns a 30-second rebuild
    into a file copy. Without git, it always rebuilds.
#>

function Get-SourceStamp([string]$repo) {
    # The git tree of the working folder as it is now (tracked and new files, minus .gitignore'd
    # ones), built in a scratch index so the real one is untouched. Same files, same stamp, whether
    # or not they've been committed.
    $index = [IO.Path]::GetTempFileName()
    try {
        $gitDir = git -C $repo rev-parse --absolute-git-dir 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $gitDir) { return $null }
        Copy-Item (Join-Path $gitDir 'index') $index -Force
        $env:GIT_INDEX_FILE = $index
        git -C $repo add -A 2>$null | Out-Null
        $tree = git -C $repo write-tree 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return $tree
    }
    catch {
        return $null
    }
    finally {
        Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue
        Remove-Item $index -Force -ErrorAction SilentlyContinue
    }
}

<#
.SYNOPSIS
    Makes sure artifacts\publish holds a Release build of the current source; returns its path.
#>
function Publish-Ledger([string]$repo, [switch]$Force) {
    $out = Join-Path $repo 'artifacts\publish'
    $stampFile = Join-Path $repo 'artifacts\publish.stamp' # beside the build, so it's never installed
    $stamp = Get-SourceStamp $repo

    if (-not $Force -and $stamp -and (Test-Path (Join-Path $out 'Ledger.exe')) -and (Test-Path $stampFile) -and
        ((Get-Content $stampFile -Raw).Trim() -eq $stamp)) {
        Write-Host 'Source unchanged since the last build; reusing it.'
        return $out
    }

    Write-Host 'Publishing Release build (x64, trimmed)...'
    if (Test-Path $stampFile) { Remove-Item $stampFile -Force }
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $repo 'src\Ledger.App\Ledger.App.csproj') `
        -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $out -nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if ($stamp) { Set-Content $stampFile $stamp -Encoding ascii }
    return $out
}
