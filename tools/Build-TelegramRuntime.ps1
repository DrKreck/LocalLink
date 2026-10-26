param([Parameter(Mandatory=$true)][string]$OutputDirectory, [Parameter(Mandatory=$true)][string]$SourceDirectory)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath($SourceDirectory)
$runtimeRoot=[IO.Path]::GetFullPath($OutputDirectory)
$sourcePrefix=$sourceRoot.TrimEnd([char[]]'\/')+[IO.Path]::DirectorySeparatorChar
if ($runtimeRoot.TrimEnd([char[]]'\/') -eq $sourceRoot.TrimEnd([char[]]'\/') -or $runtimeRoot.StartsWith($sourcePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime output must be separate from source.' }
if (Test-Path -LiteralPath $runtimeRoot) { throw 'Runtime output directory must not already exist. Choose a new directory.' }
$python=Join-Path $sourceRoot 'python.exe'
$version=& $python -I -B -c 'import sys; print(sys.version.split()[0])'
if ($LASTEXITCODE -ne 0 -or $version -ne '3.12.14') { throw 'This reviewed runtime requires CPython 3.12.14.' }
$versions=& $python -I -B -c 'import importlib.metadata as m; print(m.version("cryptography")+","+m.version("cffi")+","+m.version("pycparser"))'
if ($LASTEXITCODE -ne 0 -or $versions -ne '50.0.1,2.1.1,3.0') { throw 'Crypto dependency versions differ from reviewed runtime.' }
New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
foreach($file in @('python.exe','python3.dll','python312.dll','vcruntime140.dll','vcruntime140_1.dll','LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination $runtimeRoot -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\telegram_bridge\python312._pth') -Destination $runtimeRoot -Force
Add-Type -AssemblyName System.IO.Compression
$zipPath=Join-Path $runtimeRoot 'python312.zip'
$stream=[IO.File]::Open($zipPath,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
$archive=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
try {
    $libRoot=Join-Path $sourceRoot 'Lib'
    foreach($file in Get-ChildItem -LiteralPath $libRoot -File -Recurse) {
        $relative=$file.FullName.Substring($libRoot.Length+1).Replace('\','/')
        if ($relative -match '^(site-packages|test|idlelib|tkinter|turtledemo|ensurepip|venv)/' -or $relative -match '(^|/)__pycache__/|\.pyc$') { continue }
        $entry=$archive.CreateEntry($relative,[IO.Compression.CompressionLevel]::Optimal)
        $input=[IO.File]::OpenRead($file.FullName)
        $output=$entry.Open()
        try { $input.CopyTo($output) } finally { $output.Dispose();$input.Dispose() }
    }
} finally { $archive.Dispose();$stream.Dispose() }
$dllRoot=Join-Path $runtimeRoot 'DLLs'
New-Item -ItemType Directory -Path $dllRoot -Force | Out-Null
foreach($name in @('_asyncio.pyd','_bz2.pyd','_ctypes.pyd','_decimal.pyd','_elementtree.pyd','_hashlib.pyd','_lzma.pyd','_overlapped.pyd','_queue.pyd','_socket.pyd','_ssl.pyd','_uuid.pyd','_wmi.pyd','_zoneinfo.pyd','libcrypto-3-x64.dll','libffi-8.dll','libssl-3-x64.dll','pyexpat.pyd','select.pyd','unicodedata.pyd')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot ('DLLs\'+$name)) -Destination $dllRoot -Force
}
$packages=Join-Path $runtimeRoot 'Lib\site-packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null
foreach($name in @('cryptography','cryptography-50.0.1.dist-info','cffi','cffi-2.1.1.dist-info','pycparser','pycparser-3.0.dist-info','_cffi_backend.cp312-win_amd64.pyd')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot ('Lib\site-packages\'+$name)) -Destination $packages -Recurse -Force
}
& (Join-Path $runtimeRoot 'python.exe') -I -B -c 'import asyncio,ssl,socket,hashlib,json; from cryptography.hazmat.primitives.ciphers import Cipher,algorithms,modes; assert len(Cipher(algorithms.AES(bytes(32)),modes.CTR(bytes(16))).encryptor().update(bytes(64)))==64; c=ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT);c.load_default_certs();assert c.check_hostname and c.keylog_filename is None;print("Isolated runtime and strict crypto/TLS imports passed")'
if ($LASTEXITCODE -ne 0) { throw 'Packaged runtime verification failed.' }
$files=@(Get-ChildItem -LiteralPath $runtimeRoot -File -Recurse | Where-Object Name -ne 'runtime-manifest.json' | ForEach-Object {
    [ordered]@{Path=$_.FullName.Substring($runtimeRoot.Length+1).Replace('\','/');Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
[ordered]@{Python=$version;Cryptography='50.0.1';Cffi='2.1.1';Pycparser='3.0';Files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtimeRoot 'runtime-manifest.json') -Encoding UTF8
Write-Host ('Packaged isolated runtime: '+$files.Count+' files; no installation or downloads.')
