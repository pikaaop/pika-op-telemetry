param(
    [string]$Customer,
    [ValidateRange(1,36500)][int]$Days=30,
    [string]$InstallationId='',
    [string]$OutputPath=''
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($Customer)){$Customer=Read-Host 'License customer name'}
if([string]::IsNullOrWhiteSpace($Customer) -or $Customer.Length -gt 160){throw 'Enter a customer name of 1–160 characters.'}
if($InstallationId -and $InstallationId -notmatch '^[a-f0-9]{32}$'){throw 'Installation ID must match the random ID displayed by the activation window.'}
$parameters=New-Object Security.Cryptography.CspParameters
$parameters.ProviderType=24
$parameters.KeyContainerName='PikaOp.LicenseSigning.v1'
$parameters.Flags=[Security.Cryptography.CspProviderFlags]::UseExistingKey
$signer=New-Object Security.Cryptography.RSACryptoServiceProvider($parameters)
$signer.PersistKeyInCsp=$true
$now=[DateTimeOffset]::UtcNow
$claims=[ordered]@{Product='pika op';Id=[Guid]::NewGuid().ToString();Customer=$Customer;NotBeforeUnix=$now.AddMinutes(-5).ToUnixTimeSeconds();ExpiresUnix=$now.AddDays($Days).ToUnixTimeSeconds();InstallationId=$InstallationId}
$payload=[Text.Encoding]::UTF8.GetBytes(($claims|ConvertTo-Json -Compress))
$signature=$signer.SignData($payload,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
$signer.Dispose()
function ConvertTo-Base64Url([byte[]]$Bytes){return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+','-').Replace('/','_')}
$token='PIKA1.'+(ConvertTo-Base64Url $payload)+'.'+(ConvertTo-Base64Url $signature)
if(-not $OutputPath){$ownerFolder=Join-Path $PSScriptRoot 'owner-licenses';New-Item -ItemType Directory -Path $ownerFolder -Force|Out-Null;$OutputPath=Join-Path $ownerFolder ($claims.Id+'.txt')}
$stream=[IO.File]::Open($OutputPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
$writer=New-Object IO.StreamWriter($stream)
try{$writer.Write($token)}finally{$writer.Dispose()}
Write-Output ('License created: '+$OutputPath)
Write-Output ('Expires UTC: '+$now.AddDays($Days).ToString('o'))
Write-Output 'Keep this owner tool private. Share only the generated activation key with its intended customer.'
