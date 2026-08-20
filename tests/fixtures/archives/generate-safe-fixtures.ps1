[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$fixtureDirectory = $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $fixtureDirectory '..\..\..')).Path
$workerProject = Join-Path $repositoryRoot 'src\ReachCommander.ArchiveWorker\ReachCommander.ArchiveWorker.csproj'
$workerOutput = Join-Path $repositoryRoot 'src\ReachCommander.ArchiveWorker\bin\Release\net10.0'

& dotnet build $workerProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Could not build the archive worker (exit code $LASTEXITCODE)."
}

$sharpCompressAssembly = Join-Path $workerOutput 'SharpCompress.dll'
if (-not (Test-Path -LiteralPath $sharpCompressAssembly -PathType Leaf)) {
    throw "SharpCompress 0.50.4 was not found at the expected worker output path."
}
Add-Type -Path $sharpCompressAssembly

$timestamp = [DateTimeOffset]::Parse(
    '2000-01-01T00:00:00Z',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal)
$encoding = [Text.UTF8Encoding]::new($false)
$entries = [ordered]@{
    'root.txt' = "root fixture`n"
    'Family/2025/photo.txt' = "photo fixture`n"
    'Family/2025/nested.zip' = "nested archive marker`n"
}

$zipPath = Join-Path $fixtureDirectory 'nested.zip'
$zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $zip = [IO.Compression.ZipArchive]::new(
        $zipStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $true,
        $encoding)
    try {
        foreach ($item in $entries.GetEnumerator()) {
            $entry = $zip.CreateEntry($item.Key, [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = $timestamp
            $entryStream = $entry.Open()
            try {
                $bytes = $encoding.GetBytes($item.Value)
                $entryStream.Write($bytes, 0, $bytes.Length)
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    $zipStream.Dispose()
}

$sevenZipPath = Join-Path $fixtureDirectory 'sample.7z'
$sevenZipStream = [IO.File]::Open(
    $sevenZipPath,
    [IO.FileMode]::Create,
    [IO.FileAccess]::ReadWrite,
    [IO.FileShare]::None)
try {
    $options = [SharpCompress.Writers.SevenZip.SevenZipWriterOptions]::new(
        [SharpCompress.Common.CompressionType]::LZMA2)
    $writer = [SharpCompress.Writers.SevenZip.SevenZipWriter]::new($sevenZipStream, $options)
    try {
        foreach ($item in $entries.GetEnumerator()) {
            $bytes = $encoding.GetBytes($item.Value)
            $content = [IO.MemoryStream]::new($bytes, $false)
            try {
                $writer.Write($item.Key, $content, $timestamp.UtcDateTime)
            }
            finally {
                $content.Dispose()
            }
        }
    }
    finally {
        [void]$writer.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }
}
finally {
    $sevenZipStream.Dispose()
}

$zipBytes = [IO.File]::ReadAllBytes($zipPath)
$chunkSize = [int][Math]::Ceiling($zipBytes.Length / 3.0)
for ($index = 0; $index -lt 3; $index++) {
    $offset = $index * $chunkSize
    $length = [Math]::Min($chunkSize, $zipBytes.Length - $offset)
    $part = [byte[]]::new($length)
    [Array]::Copy($zipBytes, $offset, $part, 0, $length)
    $partPath = Join-Path $fixtureDirectory ('split.zip.{0:D3}' -f ($index + 1))
    [IO.File]::WriteAllBytes($partPath, $part)
}

$generatedNames = @(
    'nested.zip',
    'sample.7z',
    'split.zip.001',
    'split.zip.002',
    'split.zip.003'
)
$generatedNames |
    ForEach-Object { Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $fixtureDirectory $_) } |
    Select-Object @{ Name = 'File'; Expression = { Split-Path $_.Path -Leaf } }, Hash
