param(
    [Parameter(Mandatory=$true)]
    [string[]] $appFiles,
    [string[]] $includeOnlyAppIds
)

if (-not $appFiles) {
    return @()
}

$apps = @()
$files = @{}

foreach ($appFile in $appFiles) {
    $appJson = al GetPackageManifest $appFile | ConvertFrom-Json
    $key = "$($appJson.Id):$($appJson.Version)"
    if (-not $files.ContainsKey($key)) {
        $files[$key] = $appFile
        $apps += @($appJson)
    }
}

$script:sortedApps = @()
$script:unknownDependencies = @()

function AddAnApp {
    param($anApp)
    $alreadyAdded = $script:sortedApps | Where-Object { $_.Id -eq $anApp.Id -and $_.Version -eq $anApp.Version }
    if (-not $alreadyAdded) {
        AddDependencies -anApp $anApp
        $script:sortedApps += $anApp
    }
}

function AddDependency {
    param($dependency)
    $dependencyAppId = if ($dependency.PSObject.Properties.Name -eq 'AppId') { $dependency.AppId } else { $dependency.Id }
    $dependentApp = $apps | Where-Object { $_.Id -eq $dependencyAppId } | Sort-Object -Property @{ Expression = { [System.Version]$_.Version } }
    if ($dependentApp) {
        if ($dependentApp -is [Array]) {
            $dependentApp = $dependentApp | Select-Object -Last 1
        }
        AddAnApp -anApp $dependentApp
    } else {
        $script:unknownDependencies += $dependencyAppId
    }
}

function AddDependencies {
    param($anApp)
    if ($anApp -and ($anApp.PSObject.Properties.Name -eq 'Dependencies') -and $anApp.Dependencies) {
        $anApp.Dependencies | ForEach-Object { AddDependency -dependency $_ }
    }
}

function GetAppWithDependencies {
    param([string] $appId)
    $result = @($appId)
    $app = $apps | Where-Object { $_.Id -eq $appId } | Select-Object -Last 1
    if ($app -and ($app.PSObject.Properties.Name -eq 'Dependencies') -and $app.Dependencies) {
        foreach ($dep in $app.Dependencies) {
            $depAppId = if ($dep.PSObject.Properties.Name -eq 'AppId') { $dep.AppId } else { $dep.Id }
            $result += GetAppWithDependencies -appId $depAppId
        }
    }
    return $result | Select-Object -Unique
}

$apps | Where-Object { $_.Name -eq 'Application' } | ForEach-Object { AddAnApp -anApp $_ }
$apps | ForEach-Object { AddAnApp -anApp $_ }

$filteredApps = if ($includeOnlyAppIds) {
    $expandedAppIds = $includeOnlyAppIds | ForEach-Object { GetAppWithDependencies -appId $_ } | Select-Object -Unique
    $script:sortedApps | Where-Object { $_.Id -in $expandedAppIds }
} else {
    $script:sortedApps
}

return @{
    SortedApps = @($filteredApps | ForEach-Object { $files["$($_.Id):$($_.Version)"] })
    UnknownDependencies = @($script:unknownDependencies | Select-Object -Unique)
}
