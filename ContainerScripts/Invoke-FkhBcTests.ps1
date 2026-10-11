[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RequestBase64,

    [Parameter(Mandatory = $true)]
    [string] $ResultPath
)

$ErrorActionPreference = 'Stop'
$clientContext = $null
$sslVerificationDisabled = $false
$maxJUnitBytes = 10MB

try {
    $requestJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64))
    $request = $requestJson | ConvertFrom-Json
    $tenant = [string]$request.Tenant
    $extensionId = [Guid]::Parse([string]$request.ExtensionId)
    $appName = [string]$request.AppName
    $testCodeunitRange = [string]$request.TestCodeunitRange
    $interactionTimeout = [TimeSpan]::FromMinutes([int]$request.TimeoutMinutes)

    . 'C:\run\prompt.ps1' -silent

    $installedApps = @(Get-NAVAppInfo -ServerInstance $ServerInstance -Tenant $tenant -TenantSpecificProperties)
    $testRunnerId = [Guid]'23de40a6-dfe8-4f80-80db-d70f83ce8caf'
    if (-not ($installedApps | Where-Object { $_.AppId -eq $testRunnerId -and $_.IsInstalled })) {
        throw "The Test Runner app is not installed for tenant '$tenant'."
    }

    $testApp = $installedApps | Where-Object { $_.AppId -eq $extensionId -and $_.IsInstalled } | Select-Object -First 1
    if ($null -eq $testApp) {
        throw "Test app '$extensionId' is not installed for tenant '$tenant'."
    }
    if (-not [string]::IsNullOrWhiteSpace($appName) -and $testApp.Name -ne $appName) {
        throw "Test app '$extensionId' is installed as '$($testApp.Name)', not '$appName'."
    }

    $clientDllPath = 'C:\Test Assemblies\Microsoft.Dynamics.Framework.UI.Client.dll'
    if (-not (Test-Path -LiteralPath $clientDllPath -PathType Leaf)) {
        throw "The Business Central UI test client assembly is missing at '$clientDllPath'."
    }

    $serviceFolderItem = Get-Item 'C:\Program Files\Microsoft Dynamics NAV\*\Service' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $serviceFolderItem) {
        throw 'The Business Central service folder is missing.'
    }
    $serviceFolder = $serviceFolderItem.FullName
    $newtonsoftDllPath = Join-Path $serviceFolder 'Management\Newtonsoft.Json.dll'
    if (-not (Test-Path -LiteralPath $newtonsoftDllPath -PathType Leaf)) {
        $newtonsoftDllPath = Join-Path $serviceFolder 'Newtonsoft.Json.dll'
    }
    if (-not (Test-Path -LiteralPath $newtonsoftDllPath -PathType Leaf)) {
        throw 'The Business Central Newtonsoft.Json assembly is missing.'
    }

    $customSettingsPath = Join-Path $serviceFolder 'CustomSettings.config'
    [xml]$customSettings = [IO.File]::ReadAllText($customSettingsPath)
    $publicWebBaseUrl = $customSettings.SelectSingleNode("//appSettings/add[@key='PublicWebBaseUrl']").Value.TrimEnd('/')
    $credentialType = $customSettings.SelectSingleNode("//appSettings/add[@key='ClientServicesCredentialType']").Value
    if ($credentialType -ne 'NavUserPassword') {
        throw "Container test execution does not support '$credentialType' client authentication."
    }
    if ([string]::IsNullOrWhiteSpace($env:username) -or [string]::IsNullOrWhiteSpace($env:password)) {
        throw 'The container administrator credentials are unavailable.'
    }

    $uri = [Uri]$publicWebBaseUrl
    $serviceUrl = "$($uri.Scheme)://localhost:$($uri.Port)$($uri.AbsolutePath.TrimEnd('/'))/cs?tenant=$([Uri]::EscapeDataString($tenant))"
    $securePassword = New-Object Security.SecureString
    foreach ($character in $env:password.ToCharArray()) {
        $securePassword.AppendChar($character)
    }
    $securePassword.MakeReadOnly()
    $credential = New-Object Management.Automation.PSCredential($env:username, $securePassword)
    $runnerFolder = 'C:\run\my\BcContainerHelper-6.1.15'
    $testFunctionsPath = Join-Path $runnerFolder 'PsTestFunctions.ps1'
    $clientContextPath = Join-Path $runnerFolder 'ClientContext.ps1'
    if (-not (Test-Path -LiteralPath $testFunctionsPath) -or -not (Test-Path -LiteralPath $clientContextPath)) {
        throw 'The pinned BcContainerHelper 6.1.15 test runner is missing.'
    }

    . $testFunctionsPath `
        -newtonSoftDllPath $newtonsoftDllPath `
        -clientDllPath $clientDllPath `
        -clientContextScriptPath $clientContextPath

    Disable-SslVerification
    $sslVerificationDisabled = $true
    Write-Host "Connecting to the local Business Central test service for tenant '$tenant'."
    $clientContext = New-ClientContext `
        -serviceUrl $serviceUrl `
        -auth 'NavUserPassword' `
        -credential $credential `
        -interactionTimeout $interactionTimeout

    Run-Tests `
        -clientContext $clientContext `
        -testPage 130455 `
        -testSuite 'DEFAULT' `
        -extensionId $extensionId.ToString() `
        -appName $appName `
        -testCodeunitRange $testCodeunitRange `
        -JUnitResultFileName $ResultPath `
        -detailed | Out-Null

    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw 'Business Central test execution did not create JUnit.'
    }
    $junitFile = Get-Item -LiteralPath $ResultPath
    if ($junitFile.Length -gt $maxJUnitBytes) {
        throw "Business Central test execution created JUnit larger than $maxJUnitBytes bytes."
    }
    $junitBytes = [IO.File]::ReadAllBytes($ResultPath)
    if ($junitBytes.Length -eq 0) {
        throw 'Business Central test execution created empty JUnit.'
    }
    $junitSettings = New-Object Xml.XmlReaderSettings
    $junitSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $junitSettings.XmlResolver = $null
    $junitStream = New-Object IO.MemoryStream(,$junitBytes)
    $junitReader = [Xml.XmlReader]::Create($junitStream, $junitSettings)
    $junit = New-Object Xml.XmlDocument
    $junit.XmlResolver = $null
    try {
        $junit.Load($junitReader)
    } finally {
        $junitReader.Dispose()
        $junitStream.Dispose()
    }
    if ($junit.DocumentElement.LocalName -notin @('testsuite', 'testsuites')) {
        throw 'Business Central test execution created unsupported JUnit.'
    }

    Write-Output "FKH_JUNIT_BASE64:$([Convert]::ToBase64String($junitBytes))"
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 2
} finally {
    if ($null -ne $clientContext) {
        Remove-ClientContext -clientContext $clientContext
    }
    if ($sslVerificationDisabled) {
        Enable-SslVerification
    }
    Remove-Item -LiteralPath $ResultPath -Force -ErrorAction SilentlyContinue
}