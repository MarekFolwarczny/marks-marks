<#
.SYNOPSIS
Build a standalone Windows EXE and XML documentation from the C# sources.
.DESCRIPTION
Use the system .NET Framework compiler and Windows WPF assemblies. Create
app.ico, embed graphic assets, and generate MarksMarks.xml for IDE support.
This script builds locally without modifying user notes.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.OUTPUTS
MarksMarks.exe, MarksMarks.xml, and app.ico in the source directory.
#>
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $compiler)) { throw '.NET Framework 4.x is required.' }
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$mediaReferences = '/reference:"' + (Join-Path $wpf 'PresentationCore.dll') + '" /reference:"' + (Join-Path $wpf 'PresentationFramework.dll') + '" /reference:"' + (Join-Path $wpf 'WindowsBase.dll') + '" /reference:"' + (Join-Path $wpf 'WindowsFormsIntegration.dll') + '"'
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'make-icon.ps1')
    $compile = Start-Process -FilePath $compiler -WorkingDirectory $PSScriptRoot -ArgumentList ($mediaReferences + ' /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32manifest:app.manifest /win32icon:app.ico /resource:brand-logo.png,MarksMarks.BrandLogo /resource:LICENSE,MarksMarks.License /resource:assets\icon.png,MarksMarks.BrandIcon /resource:assets\metal.png,MarksMarks.Metal /resource:assets\header.png,MarksMarks.Header /doc:MarksMarks.xml /out:MarksMarks.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Xaml.dll /reference:System.Core.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll Program.cs NotesStore.cs NotesTextBox.cs BrandControls.cs ImageViewer.cs ProjectArchive.cs LayeredOverlay.cs UiText.cs VideoFormats.cs VideoViewer.cs') -NoNewWindow -Wait -PassThru
    if ($compile.ExitCode -ne 0) { throw 'Application compilation failed.' }
    Write-Host 'Built: MarksMarks.exe + MarksMarks.xml'
} finally { Pop-Location }
