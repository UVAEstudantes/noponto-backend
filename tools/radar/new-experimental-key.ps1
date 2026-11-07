param([string]$Directory = (Join-Path $env:LOCALAPPDATA 'NoPonto/RadarExperimental'))
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Execute no PowerShell Windows; no Debian use diretório 0700 e arquivo 0600 do usuário da API.' }
$target = [System.IO.Path]::GetFullPath($Directory)
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($target.StartsWith($repository, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Chave deve ficar fora do repositório.' }
$keyPath = Join-Path $target 'push-encryption.key'
if (Test-Path -LiteralPath $keyPath) { throw 'Chave já existe. Não sobrescrever: isso impediria recuperação de tokens.' }
New-Item -ItemType Directory -Force -Path $target | Out-Null
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [System.Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($identity)
$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $target -AclObject $acl
$bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
try { [System.IO.File]::WriteAllText($keyPath, [Convert]::ToBase64String($bytes)) }
finally { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
Write-Output "Chave criada com ACL restrita: $keyPath"
Write-Output 'Configure Radar__Push__EncryptionKeyFile com esse caminho. Não imprima nem copie a chave para Git ou logs.'
