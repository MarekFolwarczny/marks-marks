<#
.SYNOPSIS
Compile and run regression tests in an isolated GUID directory.
.DESCRIPTION
Require Windows with .NET Framework/WPF and an interactive desktop for UI/playback.
Create isolated files and previews in tests\output. Preserve user notes and
the system clipboard; test windows and hotkeys belong to the test process.
Return a nonzero exit code when compilation or any test fails.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test.ps1
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test.ps1 -AttachmentsOnly
#>
param([switch]$AttachmentsOnly,
    [ValidateSet('All','Storage','Legacy','Window','Design','Actions','Viewer','Archive','Languages','Videos','Attachments','Regression')][string]$Suite = 'All')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$output = Join-Path $PSScriptRoot 'output'
New-Item $output -ItemType Directory -Force | Out-Null
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$mediaReferences = '/reference:"' + (Join-Path $wpf 'PresentationCore.dll') + '" /reference:"' + (Join-Path $wpf 'PresentationFramework.dll') + '" /reference:"' + (Join-Path $wpf 'WindowsBase.dll') + '" /reference:"' + (Join-Path $wpf 'WindowsFormsIntegration.dll') + '"'
Push-Location $root
try {
    $compile = Start-Process -FilePath $compiler -WorkingDirectory $root -ArgumentList ($mediaReferences + ' /nologo /target:exe /main:Tests /codepage:65001 /win32manifest:app.manifest /win32icon:app.ico /resource:brand-logo.png,MarksMarks.BrandLogo /resource:LICENSE,MarksMarks.License /resource:assets\icon.png,MarksMarks.BrandIcon /resource:assets\metal.png,MarksMarks.Metal /resource:assets\header.png,MarksMarks.Header /out:tests\output\Tests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Xaml.dll /reference:System.Core.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll Program.cs NotesStore.cs NotesTextBox.cs BrandControls.cs ImageViewer.cs ProjectArchive.cs LayeredOverlay.cs UiText.cs VideoFormats.cs VideoViewer.cs tests\Tests.cs tests\RegressionTests.cs') -NoNewWindow -Wait -PassThru
    if ($compile.ExitCode -ne 0) { throw 'Compilation of tests failed.' }
    $run = Join-Path $output ([Guid]::NewGuid().ToString('N'))
    $suites = if ($AttachmentsOnly) { @('Attachments') } elseif ($Suite -eq 'All') { @('Storage','Legacy','Window','Design','Actions','Viewer','Archive','Languages','Videos','Attachments','Regression') } else { @($Suite) }
    # Independent suite processes release their own native UI and media resources.
    foreach ($selected in $suites) {
        Write-Host ('SUITE: ' + $selected)
        $test = Start-Process -FilePath (Join-Path $output 'Tests.exe') -ArgumentList ('"' + $run + '" ' + $selected) -NoNewWindow -Wait -PassThru
        if ($test.ExitCode -ne 0) { throw ('Suite ' + $selected + ' failed. Native exit code: ' + $test.ExitCode) }
    }
    foreach ($preview in @('preview.png','overflow.png','viewer.png','english.png')) {
        if (Test-Path (Join-Path $run $preview)) { Copy-Item (Join-Path $run $preview) (Join-Path $output $preview) -Force }
    }
} finally { Pop-Location }
