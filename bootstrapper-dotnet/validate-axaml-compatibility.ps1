[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'M2M.XmlEditor.Bootstrapper.csproj'
$expectedAvaloniaVersion = '12.1.2'
$violations = New-Object System.Collections.Generic.List[string]

if (-not (Test-Path -LiteralPath $projectFile)) {
    Write-Host "HIBA: A projektfajl nem talalhato: $projectFile" -ForegroundColor Red
    exit 1
}

[xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
$avaloniaReferences = @($projectXml.Project.ItemGroup.PackageReference | Where-Object { $_.Include -like 'Avalonia*' })
foreach ($reference in $avaloniaReferences) {
    if ([string]$reference.Version -ne $expectedAvaloniaVersion) {
        $violations.Add("M2M.XmlEditor.Bootstrapper.csproj: $($reference.Include) verzioja '$($reference.Version)', az ellenorzo baseline-ja '$expectedAvaloniaVersion'. Frissitsd az ellenorzest a komponensverzio valtoztatasaval egyutt.")
    }
}

$axamlFiles = Get-ChildItem -Path $projectRoot -Filter '*.axaml' -File -Recurse | Where-Object {
    $relative = $_.FullName.Substring($projectRoot.Length).ToLowerInvariant()
    -not ($relative.Contains('\bin\') -or
          $relative.Contains('\obj\') -or
          $relative.Contains('\.build\') -or
          $relative.Contains('\dist\') -or
          $relative.Contains('\embeddedassets\'))
}

foreach ($file in $axamlFiles) {
    $relativePath = $file.FullName.Substring($projectRoot.Length).TrimStart('\')
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = $null

    try {
        $reader = [System.Xml.XmlReader]::Create($file.FullName, $settings)
        while ($reader.Read()) {
            if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element -or -not $reader.HasAttributes) {
                continue
            }

            $elementName = $reader.LocalName
            while ($reader.MoveToNextAttribute()) {
                $attributeName = $reader.LocalName
                $lineInfo = [System.Xml.IXmlLineInfo]$reader
                $lineNumber = if ($lineInfo.HasLineInfo()) { $lineInfo.LineNumber } else { 0 }
                $location = if ($lineNumber -gt 0) { "$relativePath($lineNumber)" } else { $relativePath }

                if ($elementName -eq 'RadioButton' -and $attributeName -eq 'Checked') {
                    $violations.Add("${location}: RadioButton 'Checked' AXAML event nem kompatibilis a projekt Avalonia $expectedAvaloniaVersion baseline-javal. Hasznald a 'Click' esemenyt vagy kodbol kezeld az IsChecked allapotot.")
                }

                if ($elementName -eq 'TextBox' -and $attributeName -eq 'Watermark') {
                    $violations.Add("${location}: TextBox 'Watermark' elavult az Avalonia $expectedAvaloniaVersion baseline-ban. Hasznald a 'PlaceholderText' property-t.")
                }
            }
            [void]$reader.MoveToElement()
        }
    }
    catch {
        $violations.Add("${relativePath}: az AXAML XML ellenorzese sikertelen: $($_.Exception.Message)")
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host 'HIBA: Avalonia AXAML kompatibilitasi ellenorzes sikertelen.' -ForegroundColor Red
    foreach ($violation in $violations) {
        Write-Host "  $violation" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Avalonia AXAML kompatibilitasi ellenorzes sikeres (baseline: Avalonia $expectedAvaloniaVersion)."
exit 0
