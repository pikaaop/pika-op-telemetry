param([string]$OutputFolder='release')
$ErrorActionPreference='Stop'
$sourceFolder=$PSScriptRoot
$releaseFolder=Join-Path $sourceFolder $OutputFolder
New-Item -ItemType Directory -Path $releaseFolder -Force | Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler is required.' }
$parameters=New-Object System.Security.Cryptography.CspParameters
$parameters.ProviderType=24
$parameters.KeyContainerName='PikaOp.ReleaseSigning.v1'
$signer=New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072,$parameters)
$signer.PersistKeyInCsp=$true
$publicXml=$signer.ToXmlString($false)
$escapedPublic=$publicXml.Replace('"','""')
$keySource='namespace PikaOp { internal static class ReleaseKey { internal const string PublicXml = @"'+$escapedPublic+'"; } }'
[IO.File]::WriteAllText((Join-Path $sourceFolder 'ReleaseKey.cs'),$keySource)
$references=@('System.dll','System.Core.dll','System.Security.dll','System.Web.Extensions.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Windows.Forms.dll','System.Drawing.dll')
Add-Type -AssemblyName System.Drawing
$iconPath=Join-Path $sourceFolder 'pika_op.ico'
$bitmap=New-Object Drawing.Bitmap(256,256)
$graphics=[Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.ColorTranslator]::FromHtml('#091018'))
$brush=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#54E3B0'))
$font=New-Object Drawing.Font('Segoe UI',160,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
$graphics.DrawString('p',$font,$brush,45,-5)
$graphics.FillEllipse($brush,172,180,26,26)
$nativeIcon=$bitmap.GetHicon()
$icon=[Drawing.Icon]::FromHandle($nativeIcon)
$iconStream=[IO.File]::Create($iconPath)
$icon.Save($iconStream)
$iconStream.Dispose()
$icon.Dispose()
$font.Dispose()
$brush.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/debug-','/langversion:5',('/out:'+(Join-Path $releaseFolder 'pika_op.exe')),('/win32manifest:'+(Join-Path $sourceFolder 'app.manifest')),('/resource:'+(Join-Path $sourceFolder 'MainWindow.xaml')+',PikaOp.MainWindow.xaml'))
$arguments+=('/win32icon:'+$iconPath)
foreach($reference in $references){
    $referencePath=Join-Path $framework $reference
    if(-not (Test-Path -LiteralPath $referencePath)){$referencePath=Join-Path (Join-Path $framework 'WPF') $reference}
    $arguments+=('/reference:'+$referencePath)
}
$dumpFolder=Join-Path $env:USERPROFILE 'Downloads\cs2-dumper-main\cs2-dumper-main\output'
foreach($name in @('offsets.json','client_dll.json','info.json')){
    $dumpFile=Join-Path $dumpFolder $name
    if(-not (Test-Path -LiteralPath $dumpFile)){throw ('Missing bundled dump file: '+$dumpFile)}
    $arguments+=('/resource:'+$dumpFile+',PikaOp.Dump.'+$name)
}
$arguments+=Get-ChildItem -LiteralPath $sourceFolder -Filter '*.cs' | Where-Object {$_.Name -notin @('Licensing.cs','LicenseKey.cs')} | Select-Object -ExpandProperty FullName
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'C# compilation failed'}
$exe=Join-Path $releaseFolder 'pika_op.exe'
$manifest=[ordered]@{product='pika op';version='1.5.0';created_utc=[DateTime]::UtcNow.ToString('o');sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()}
$manifestBytes=[Text.Encoding]::UTF8.GetBytes(($manifest|ConvertTo-Json -Compress))
$signature=$signer.SignData($manifestBytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
$stream=[IO.File]::Open($exe,[IO.FileMode]::Append,[IO.FileAccess]::Write)
$writer=New-Object IO.BinaryWriter($stream)
try{
    $writer.Write($manifestBytes)
    $writer.Write($signature)
    $writer.Write([int]$manifestBytes.Length)
    $writer.Write([int]$signature.Length)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('PIKAEND1'))
}finally{$writer.Dispose()}
$signer.Dispose()
Write-Output ('Built standalone '+$exe+' with embedded dump and signed integrity data. No activation key or sidecar files required.')
