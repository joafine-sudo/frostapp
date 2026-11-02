# Cria o frost-resources.zip (versão Frost + libraries) para enviar para o GitHub Releases.
# Corre na PowerShell, dentro da pasta do projeto (a que tem o FrostClient.csproj):
#   powershell -ExecutionPolicy Bypass -File .\make-resources.ps1

$ErrorActionPreference = "Stop"
$src   = "default_version\frost client"
$json  = Get-Content "$src\frost client.json" -Raw | ConvertFrom-Json
$stage = Join-Path $env:TEMP "frost-res-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

# 1) Versão Frost (json + jar)
$vdir = Join-Path $stage "versions\frost client"
New-Item -ItemType Directory -Force -Path $vdir | Out-Null
Copy-Item "$src\frost client.json" $vdir
Copy-Item "$src\frost client.jar"  $vdir

# 2) Libraries listadas no json que existem na pasta libraries do projeto
$missing = @()
foreach ($lib in $json.libraries) {
    $p = $lib.name.Split(':')
    if ($p.Count -lt 3) { continue }
    $cls = if ($p.Count -ge 4) { "-" + $p[3] } else { "" }
    $rel = "libraries/" + $p[0].Replace('.','/') + "/" + $p[1] + "/" + $p[2] + "/" + $p[1] + "-" + $p[2] + $cls + ".jar"
    if (Test-Path $rel) {
        $dest = Join-Path $stage $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item $rel $dest
    } else { $missing += $rel }
}

# 3) Zip final (raiz: versions/ e libraries/)
if (Test-Path "frost-resources.zip") { Remove-Item "frost-resources.zip" }
tar -a -c -f frost-resources.zip -C $stage versions libraries

"--- Conteúdo:"
tar -tf frost-resources.zip | findstr /i ".jar .json"
"--- Não encontrados (o launcher descarrega sozinho):"
$missing
"--- Tamanho (MB):"
[math]::Round((Get-Item frost-resources.zip).Length / 1MB, 1)
